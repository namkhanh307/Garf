namespace Garf.Indexer;

internal sealed class IndexWatcher : IDisposable
{
    private readonly string _root;
    private readonly string _output;
    private readonly bool _skipTs;
    private readonly string? _tsIndexer;
    private readonly int _debounceMs;
    private readonly FileSystemWatcher _watcher;
    private readonly object _gate = new();
    private System.Threading.Timer? _timer;

    public IndexWatcher(string root, string output, bool skipTs, string? tsIndexer, int debounceMs)
    {
        _root = root;
        _output = output;
        _skipTs = skipTs;
        _tsIndexer = tsIndexer;
        _debounceMs = Math.Max(50, debounceMs);

        _watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite
                | NotifyFilters.FileName
                | NotifyFilters.Size
                | NotifyFilters.CreationTime
        };
        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Deleted += OnFileEvent;
        _watcher.Renamed += OnFileEvent;
    }

    public void Start() => _watcher.EnableRaisingEvents = true;

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (Program.IsIndexedFile(_root, e.FullPath))
        {
            Schedule();
        }
    }

    private void Schedule()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = new System.Threading.Timer(_ => Rebuild(), null, _debounceMs, Timeout.Infinite);
        }
    }

    private void Rebuild()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }

        try
        {
            var summary = Program.IndexRepository(_root, _output, _skipTs, _tsIndexer);
            IndexCache.Invalidate(Path.GetFullPath(_output));
            Console.Error.WriteLine(
                $"[garf] watch: indexed {summary.SymbolCount} symbols, {summary.EdgeCount} edges -> {summary.Output}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[garf] watch: {ex.Message}");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= OnFileEvent;
        _watcher.Created -= OnFileEvent;
        _watcher.Deleted -= OnFileEvent;
        _watcher.Renamed -= OnFileEvent;
        _watcher.Dispose();
    }
}
