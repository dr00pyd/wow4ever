namespace ForeverScout.Core;

/// <summary>
/// Startar uppladdning när spelet har sparat ForeverScout.lua (vid /reload, karaktärsbyte, utloggning och avslut),
/// inte bara när spelet avslutas.
/// <list type="bullet">
/// <item>Bevakar <c>WTF\Account\*\SavedVariables\ForeverScout.lua</c> med FileSystemWatcher. Som reserv pollas filerna
/// var 30:e sekund, eftersom FSW missar händelser på vissa diskar.</item>
/// <item>Debounce: väntar tills storlek och ändringstid legat stilla i <c>quiet</c> (5 s) och kör sedan körningen.
/// Själva körningen (Uploader) kontrollerar att filen går att parsa helt och att dess sha256 skiljer sig från förra
/// lyckade uppladdningen.</item>
/// <item>Aldrig två körningar samtidigt. Kommer en ändring under en körning markeras den som "dirty", och då görs
/// exakt en körning till efteråt.</item>
/// </list>
/// Processavslut och knappen "Ladda upp" går genom samma väg (<see cref="Notify"/>), så filen skickas inte två gånger.
/// </summary>
public sealed class SyncCoordinator : IDisposable
{
    private readonly Func<IReadOnlyList<string>> _files;
    private readonly Func<CancellationToken, Task> _run;
    private readonly TimeSpan _quiet;
    private readonly TimeSpan _maxWait;
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly System.Threading.Timer _poll;
    private FileSystemWatcher? _fsw;
    private string? _watchedDir;
    private bool _active;   // en loop (debounce + körning) pågår
    private bool _dirty;    // ändring under pågående loop → ett varv till
    private string _lastStamp = "";
    private Task _loop = Task.CompletedTask;

    /// <summary>Antal körningar som startats (för tester och diagnostik).</summary>
    public int Runs { get; private set; }

    /// <param name="files">De filer som bevakas (läses om vid varje kontroll).</param>
    /// <param name="run">Själva uppladdningen. Anropas aldrig parallellt med sig själv.</param>
    public SyncCoordinator(Func<IReadOnlyList<string>> files, Func<CancellationToken, Task> run,
        TimeSpan? quiet = null, TimeSpan? poll = null, TimeSpan? maxWait = null)
    {
        _files = files;
        _run = run;
        _quiet = quiet ?? TimeSpan.FromSeconds(5);
        _maxWait = maxWait ?? TimeSpan.FromMinutes(2);
        _lastStamp = Stamp();
        var p = poll ?? TimeSpan.FromSeconds(30);
        _poll = new System.Threading.Timer(_ => PollTick(), null, p, p);
    }

    /// <summary>Bevaka en WTF\Account-katalog (byts när spelkatalogen byts; null = sluta bevaka).</summary>
    public void Watch(string? accountDir)
    {
        lock (_lock)
        {
            if (string.Equals(accountDir, _watchedDir, StringComparison.OrdinalIgnoreCase) && _fsw != null) return;
            _fsw?.Dispose();
            _fsw = null;
            _watchedDir = accountDir;
            if (string.IsNullOrEmpty(accountDir) || !SafeFs.DirectoryExists(accountDir)) return;
            try
            {
                var w = SafeFs.CreateWatcher(accountDir, "ForeverScout.lua");
                w.IncludeSubdirectories = true;
                w.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime;
                w.InternalBufferSize = 16 * 1024;
                w.Changed += (_, _) => Notify();
                w.Created += (_, _) => Notify();
                w.Renamed += (_, _) => Notify(); // WoW skriver via temporärfil + namnbyte
                w.Error += (_, _) => { lock (_lock) { _fsw?.Dispose(); _fsw = null; } Watch(accountDir); Notify(); };
                w.EnableRaisingEvents = true;
                _fsw = w;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or SafeFsException)
            {
                _fsw = null; // pollningen tar över
            }
        }
    }

    private void PollTick()
    {
        var s = Stamp();
        bool changed;
        lock (_lock) { changed = s != _lastStamp; }
        if (changed) Notify();
    }

    private string Stamp() => _files().Count + "#" + FileStability.Snapshot(_files());

    /// <summary>Något kan ha ändrats (FSW, poll, processavslut, knapp). Startar en debounce/körning eller markerar dirty.</summary>
    public void Notify()
    {
        lock (_lock)
        {
            if (_cts.IsCancellationRequested) return;
            if (_active) { _dirty = true; return; }
            _active = true;
            _dirty = false;
            _loop = Task.Run(LoopAsync);
        }
    }

    /// <summary>Väntar tills ingen körning pågår (för tester och avslut).</summary>
    public Task WhenIdleAsync()
    {
        lock (_lock) return _loop;
    }

    private async Task LoopAsync()
    {
        var ct = _cts.Token;
        try
        {
            while (true)
            {
                await FileStability.WaitUntilStableAsync(_files(), _quiet, _maxWait, ct);
                lock (_lock) { _dirty = false; _lastStamp = Stamp(); }
                Runs++;
                try { await _run(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception) { /* körningen rapporterar själv; loopen får aldrig dö */ }
                lock (_lock)
                {
                    if (!_dirty) { _active = false; return; }
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (_lock) { if (ct.IsCancellationRequested) _active = false; }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _poll.Dispose();
        lock (_lock) { _fsw?.Dispose(); _fsw = null; }
    }
}
