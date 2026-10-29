using System.IO;

namespace NEXCORE;

internal static class DiagnosticLog
{
    private static readonly object Sync = new();
    public static void Write(string operation, Exception? error = null)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(AppSettings.Folder);
                string path = Path.Combine(AppSettings.Folder, "diagnostics.log");
                if (File.Exists(path) && new FileInfo(path).Length > 2_000_000)
                    File.Move(path, path + ".old", true);
                // Never log command arguments, API keys or serialized settings.
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {operation}: {error?.GetType().Name} {error?.Message}{Environment.NewLine}");
            }
        }
        catch { /* Logging must not interrupt recovery. */ }
    }
}
