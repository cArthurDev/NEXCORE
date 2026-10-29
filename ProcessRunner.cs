using System.Diagnostics;

namespace NEXCORE;

internal static class ProcessRunner
{
    public static async Task<string> RunAsync(string executable, string arguments, TimeSpan timeout, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        using var process = Process.Start(new ProcessStartInfo(executable, arguments)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Não foi possível iniciar " + executable);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch (Exception ex) { DiagnosticLog.Write("Encerrar comando", ex); }
            await process.WaitForExitAsync().ConfigureAwait(false);
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException("O comando excedeu o tempo permitido: " + executable);
        }
        string result = await output.ConfigureAwait(false);
        string failure = await error.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{executable} terminou com código {process.ExitCode}: {failure.Trim()}");
        return result;
    }

    public static string Run(string executable, string arguments) =>
        RunAsync(executable, arguments, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
}
