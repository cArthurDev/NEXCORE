namespace NEXCORE;

internal sealed class ShutdownAbortedException : OperationCanceledException;

internal static class ShutdownWaiter
{
    internal static async Task WaitAsync(Task operation, TimeSpan interval, Func<bool> continueWaiting)
    {
        while (!operation.IsCompleted)
        {
            using var delayCancellation = new CancellationTokenSource();
            var delay = Task.Delay(interval, delayCancellation.Token);
            if (await Task.WhenAny(operation, delay) == operation)
            { delayCancellation.Cancel(); break; }
            if (!continueWaiting()) throw new ShutdownAbortedException();
        }
        await operation;
    }
}
