namespace NEXCORE;

internal sealed class OperationCoordinator
{
    private readonly object _sync = new();
    private Lease? _current;
    public bool IsBusy { get { lock (_sync) return _current != null; } }
    public string? CurrentName { get { lock (_sync) return _current?.Name; } }
    public IDisposable? TryStart(string name)
    {
        lock (_sync)
        {
            if (_current != null) return null;
            return _current = new Lease(this, name);
        }
    }
    private sealed class Lease(OperationCoordinator owner, string name) : IDisposable
    {
        public string Name { get; } = name;
        public void Dispose()
        {
            lock (owner._sync) { if (ReferenceEquals(owner._current, this)) owner._current = null; }
        }
    }
}
