using System.IO;

namespace NEXCORE;

internal sealed record TempCleanupResult(int DeletedFiles, int RemovedFolders, int Skipped, long FreedBytes, bool Cancelled);

internal static class TempCleaner
{
    internal static string UserTempPath => Path.GetFullPath(Environment.GetEnvironmentVariable("TEMP") ?? Path.GetTempPath());
    internal static string WindowsTempPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool Contains(string parent, string child) => child.Equals(parent, StringComparison.OrdinalIgnoreCase)
        || child.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal static bool IsSafeRoot(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path)) return false;
            string root = Normalize(path);
            if (root.Equals(Normalize(Path.GetPathRoot(root)!), StringComparison.OrdinalIgnoreCase)) return false;
            var protectedPaths = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.SystemDirectory,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                AppContext.BaseDirectory, AppSettings.Folder
            };
            if (protectedPaths.Where(p => !string.IsNullOrWhiteSpace(p)).Any(p => Contains(root, Normalize(p)))) return false;
            // Do not follow junctions/symbolic links, even above a selected temporary folder.
            for (var directory = new DirectoryInfo(root); directory != null; directory = directory.Parent)
                if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return false; }
    }

    internal static TempCleanupResult Preview(IEnumerable<string> roots, CancellationToken ct, TimeSpan minimumAge) =>
        Run(roots, ct, null, minimumAge, preview: true);

    internal static TempCleanupResult Clean(IEnumerable<string> roots, CancellationToken ct, Action<TempCleanupResult>? progress = null, TimeSpan? minimumAge = null) =>
        Run(roots, ct, progress, minimumAge ?? TimeSpan.FromHours(48), preview: false);

    private static TempCleanupResult Run(IEnumerable<string> roots, CancellationToken ct, Action<TempCleanupResult>? progress, TimeSpan minimumAge, bool preview)
    {
        if (minimumAge < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(minimumAge));
        DateTime cutoff = DateTime.UtcNow - minimumAge;
        int deleted = 0, directories = 0, skipped = 0; long bytes = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        TempCleanupResult Result() => new(deleted, directories, skipped, bytes, ct.IsCancellationRequested);
        void Report()
        {
            if (watch.ElapsedMilliseconds < 250) return;
            progress?.Invoke(Result()); watch.Restart();
        }
        void Visit(string directory, string root, int depth)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                string normalized = Normalize(directory);
                if (!Contains(root, normalized) || depth > 64 || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                { skipped++; return; }
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    if (ct.IsCancellationRequested) return;
                    try
                    {
                        if (!Contains(root, Normalize(entry))) { skipped++; continue; }
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            bool oldDirectory = minimumAge == TimeSpan.Zero || Directory.GetLastWriteTimeUtc(entry) <= cutoff;
                            Visit(entry, root, depth + 1);
                            if (ct.IsCancellationRequested) return;
                            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) == 0)
                            {
                                if (!preview && oldDirectory)
                                { Directory.Delete(entry, recursive: false); directories++; }
                            }
                        }
                        else
                        {
                            var file = new FileInfo(entry);
                            if (file.LastWriteTimeUtc > cutoff) { skipped++; continue; }
                            long length = file.Length;
                            if (!preview) File.Delete(entry);
                            deleted++; bytes += length;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    { skipped++; }
                    Report();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { skipped++; }
        }

        var selected = roots.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // Validate every target before deleting anything.
        if (selected.Any(path => !IsSafeRoot(path)))
            throw new InvalidOperationException("Uma pasta temporária aponta para um local protegido ou um link. A limpeza foi cancelada.");
        foreach (string root in selected.Where(path => !selected.Any(other => !path.Equals(other, StringComparison.OrdinalIgnoreCase) && Contains(other, path))))
        {
            if (ct.IsCancellationRequested) break;
            if (!Directory.Exists(root)) continue;
            Visit(root, root, 0);
        }
        return Result();
    }
}
