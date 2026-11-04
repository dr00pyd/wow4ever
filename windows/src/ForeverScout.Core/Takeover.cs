using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ForeverScout.Core;

/// <summary>Vad den körande instansen gör när en ny start knackar på.</summary>
public enum TakeoverDecision
{
    /// <summary>Visa det befintliga fönstret; nykomlingen avslutar sig.</summary>
    Show,
    /// <summary>Avsluta den körande instansen; nykomlingen tar över.</summary>
    Exit,
}

/// <summary>Resultatet för nykomlingen.</summary>
public enum TakeoverOutcome
{
    /// <summary>Den körande instansen avslutar sig – vänta på mutexen och ta över.</summary>
    TakeOver,
    /// <summary>Den körande instansen visar sitt fönster – avsluta.</summary>
    Shown,
    /// <summary>Inget svar (t.ex. en äldre 1.0.0–1.0.2 som inte känner till protokollet).</summary>
    NoAnswer,
}

public sealed record TakeoverRequest(string Version, string ExePath);

/// <summary>
/// Övertagande från en äldre instans (från 1.0.3). Nykomlingen skickar sin version och exe-sökväg över en namngiven pipe
/// (bara samma användare, samma session; ingen processlistning, ingen filåtkomst). Den körande instansen avslutar sig om
/// nykomlingen är nyare, eller om exe-sökvägen skiljer sig och nykomlingen inte är äldre. Annars visas dess fönster.
/// </summary>
public static class Takeover
{
    public const int MaxMessageBytes = 4096;

    /// <summary>Pipenamn per användare och session (pipes är globala i Windows, mutex/händelser är "Local\").</summary>
    public static string PipeName(string id)
    {
        var who = $"{Environment.UserDomainName}\\{Environment.UserName}|{System.Diagnostics.Process.GetCurrentProcess().SessionId}";
        var h = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(who)))[..16];
        return $"{id}.Takeover.{h}";
    }

    /// <summary>Beslutsregeln (testad separat).</summary>
    public static TakeoverDecision Decide(Version mine, string myExe, TakeoverRequest theirs)
    {
        if (!Version.TryParse(theirs.Version, out var their)) return TakeoverDecision.Show;
        if (their > mine) return TakeoverDecision.Exit;
        bool otherPath = !string.Equals(Norm(myExe), Norm(theirs.ExePath), StringComparison.OrdinalIgnoreCase);
        // Annan sökväg: ta över – men aldrig till en äldre version (ingen nedgradering via en gammal kopia).
        if (otherPath && their >= mine) return TakeoverDecision.Exit;
        return TakeoverDecision.Show;
    }

    private static string Norm(string p)
    {
        try { return Path.GetFullPath(p).TrimEnd('\\'); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return p; }
    }

    /// <summary>Nykomlingen: fråga den körande instansen. Väntar högst <paramref name="timeout"/> på svar.</summary>
    public static TakeoverOutcome Request(string id, TakeoverRequest me, TimeSpan timeout)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName(id), PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            pipe.Connect((int)timeout.TotalMilliseconds);
            var payload = JsonSerializer.SerializeToUtf8Bytes(new { version = me.Version, exe = me.ExePath });
            pipe.Write(payload);
            pipe.WriteByte((byte)'\n');
            pipe.Flush();
            using var cts = new CancellationTokenSource(timeout);
            var buf = new byte[16];
            var read = pipe.ReadAsync(buf, cts.Token).AsTask().GetAwaiter().GetResult();
            var answer = Encoding.ASCII.GetString(buf, 0, read).Trim();
            return answer switch { "exit" => TakeoverOutcome.TakeOver, "show" => TakeoverOutcome.Shown, _ => TakeoverOutcome.NoAnswer };
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            return TakeoverOutcome.NoAnswer;
        }
    }

    /// <summary>
    /// Den körande instansen: lyssnar på pipen i bakgrunden. <paramref name="decide"/> väljer svar, och
    /// <paramref name="act"/> anropas efter att svaret skickats (t.ex. avsluta appen eller visa fönstret).
    /// </summary>
    public static IDisposable Listen(string id, Func<TakeoverRequest, TakeoverDecision> decide, Action<TakeoverDecision> act)
    {
        var cts = new CancellationTokenSource();
        var name = PipeName(id);
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(cts.Token);
                    var req = await ReadRequest(server, cts.Token);
                    var decision = req is null ? TakeoverDecision.Show : decide(req);
                    var reply = Encoding.ASCII.GetBytes(decision == TakeoverDecision.Exit ? "exit\n" : "show\n");
                    await server.WriteAsync(reply, cts.Token);
                    await server.FlushAsync(cts.Token);
                    try { server.WaitForPipeDrain(); } catch (IOException) { }
                    act(decision);
                    if (decision == TakeoverDecision.Exit) return;
                }
                catch (OperationCanceledException) { return; }
                catch (IOException) { await Task.Delay(200); }
                catch (UnauthorizedAccessException) { await Task.Delay(1000); }
            }
        });
        return new Stopper(cts);
    }

    private static async Task<TakeoverRequest?> ReadRequest(Stream s, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var ms = new MemoryStream();
        var buf = new byte[512];
        while (ms.Length < MaxMessageBytes)
        {
            int n = await s.ReadAsync(buf, timeout.Token);
            if (n == 0) break;
            ms.Write(buf, 0, n);
            if (Array.IndexOf(buf, (byte)'\n', 0, n) >= 0) break;
        }
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(ms.ToArray()).Trim());
            var v = doc.RootElement.GetProperty("version").GetString();
            var e = doc.RootElement.GetProperty("exe").GetString();
            return v is null || e is null ? null : new TakeoverRequest(v, e);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    private sealed class Stopper(CancellationTokenSource cts) : IDisposable
    {
        public void Dispose() { try { cts.Cancel(); } catch (ObjectDisposedException) { } }
    }
}
