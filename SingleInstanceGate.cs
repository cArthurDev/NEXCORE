using System.Threading;

namespace NEXCORE;

internal sealed class SingleInstanceGate : IDisposable
{
    private readonly Mutex _mutex;
    private SingleInstanceGate(Mutex mutex) => _mutex = mutex;

    internal static SingleInstanceGate? TryAcquire(string name = @"Local\NEXCORE.SingleInstance")
    {
        var mutex = new Mutex(false, name);
        bool acquired;
        try { acquired = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        catch { mutex.Dispose(); throw; }
        if (acquired) return new SingleInstanceGate(mutex);
        mutex.Dispose(); return null;
    }

    public void Dispose()
    {
        _mutex.ReleaseMutex(); _mutex.Dispose();
    }
}
