using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace ForeverScout.Core;

/// <summary>
/// macOS: en instans per användare och övertagande, motsvarigheten till <see cref="SingleInstance"/> och
/// <see cref="Takeover"/> på Windows.
/// <list type="bullet">
/// <item><b>Lås:</b> <c>~/Library/Application Support/ForeverScout/instance.lock</c> hålls öppen med ett exklusivt
/// flock-lås (<see cref="SafeFs.TryLockFile"/>). Det släpps automatiskt om processen dör, så inga gamla lås blir kvar.</item>
/// <item><b>Kanal:</b> en Unix domain socket, <c>instance.sock</c> i samma katalog. Katalogen har rättigheterna 0700,
/// så bara den egna användaren kan ansluta. Inga nätverksportar, ingen processlistning.</item>
/// <item><b>Protokoll</b> (samma som pipen på Windows): nykomlingen skickar <c>{"version","exe"}\n</c>, den körande
/// instansen svarar <c>exit\n</c> (den avslutar sig, nykomlingen tar över) eller <c>show\n</c> (den visar sitt fönster).
/// Beslutet tas av <see cref="Takeover.Decide"/>.</item>
/// </list>
/// Det här är den ENDA filen utöver ApiClient.cs som får använda <c>Socket</c>, och bara med
/// <c>AddressFamily.Unix</c> (kontrolleras av StaticGuardTests).
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class UnixInstance : IDisposable
{
    public static string LockPath => Path.Combine(SafeFs.AppDataDir, "instance.lock");
    public static string SocketPath => Path.Combine(SafeFs.AppDataDir, "instance.sock");

    /// <summary>Tester: en egen katalog under appdata i stället för själva appdatakatalogen.</summary>
    internal static string? DirOverride { get; set; }
    private static string Lock => DirOverride is { } d ? Path.Combine(d, "instance.lock") : LockPath;
    private static string Sock => DirOverride is { } d ? Path.Combine(d, "instance.sock") : SocketPath;

    private readonly IDisposable _lock;
    private readonly Socket _server;
    private readonly string _socketPath;
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;

    private UnixInstance(IDisposable fileLock, Socket server, string socketPath)
    {
        _lock = fileLock;
        _server = server;
        _socketPath = socketPath;
    }

    /// <summary>
    /// Försöker bli den enda instansen. Null om en annan instans håller låset (efter <paramref name="waitForPrevious"/>,
    /// som används efter en uppdatering medan den gamla processen avslutas).
    /// </summary>
    public static UnixInstance? TryAcquire(TimeSpan? waitForPrevious = null)
    {
        var deadline = DateTime.UtcNow + (waitForPrevious ?? TimeSpan.Zero);
        IDisposable? held;
        while ((held = SafeFs.TryLockFile(Lock)) is null)
        {
            if (DateTime.UtcNow >= deadline) return null;
            Thread.Sleep(200);
        }
        try
        {
            // Vi håller låset, så ingen annan lyssnar: en kvarbliven socketfil (efter en krasch) tas bort.
            var sock = SafeFs.Check(Sock, SafeFs.Access.Write, isDir: false);
            SafeFs.DeleteFile(sock);
            var server = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                server.Bind(new UnixDomainSocketEndPoint(sock));
                server.Listen(4);
            }
            catch { server.Dispose(); throw; }
            return new UnixInstance(held, server, sock);
        }
        catch
        {
            held.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Den körande instansen: tar emot förfrågningar i bakgrunden. <paramref name="decide"/> väljer svar och
    /// <paramref name="act"/> anropas efter att svaret skickats. En anslutning utan giltig förfrågan ignoreras.
    /// </summary>
    public void Listen(Func<TakeoverRequest, TakeoverDecision> decide, Action<TakeoverDecision> act)
    {
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                Socket client;
                try { client = await _server.AcceptAsync(ct); }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { await Task.Delay(200); continue; }
                try
                {
                    using (client)
                    using (var stream = new NetworkStream(client, ownsSocket: false))
                    {
                        var req = await Takeover.ReadRequest(stream, ct);
                        if (req is null) continue;
                        var decision = decide(req);
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(decision == TakeoverDecision.Exit ? "exit\n" : "show\n"), ct);
                        await stream.FlushAsync(ct);
                        act(decision);
                        if (decision == TakeoverDecision.Exit) return;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (OperationCanceledException) { }
                catch (IOException) { }
                catch (SocketException) { }
            }
        });
    }

    /// <summary>Nykomlingen: fråga den körande instansen. Väntar högst <paramref name="timeout"/> på svar.</summary>
    public static TakeoverOutcome Request(TakeoverRequest me, TimeSpan timeout)
    {
        try
        {
            // Ingen filåtkomst: bara adressen (en fast sökväg i den egna appdatakatalogen, 0700) att ansluta till.
            var sock = Sock;
            using var cts = new CancellationTokenSource(timeout);
            using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            s.ConnectAsync(new UnixDomainSocketEndPoint(sock), cts.Token).AsTask().GetAwaiter().GetResult();
            using var stream = new NetworkStream(s, ownsSocket: false);
            var payload = JsonSerializer.SerializeToUtf8Bytes(new { version = me.Version, exe = me.ExePath });
            stream.Write(payload);
            stream.WriteByte((byte)'\n');
            stream.Flush();
            var buf = new byte[16];
            var read = stream.ReadAsync(buf, cts.Token).AsTask().GetAwaiter().GetResult();
            var answer = Encoding.ASCII.GetString(buf, 0, read).Trim();
            return answer switch { "exit" => TakeoverOutcome.TakeOver, "show" => TakeoverOutcome.Shown, _ => TakeoverOutcome.NoAnswer };
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or SafeFsException or UnauthorizedAccessException)
        {
            return TakeoverOutcome.NoAnswer;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        _server.Dispose();
        try { SafeFs.DeleteFile(_socketPath); } catch (IOException) { } catch (SafeFsException) { } catch (UnauthorizedAccessException) { }
        _lock.Dispose();
        _cts.Dispose();
    }
}
