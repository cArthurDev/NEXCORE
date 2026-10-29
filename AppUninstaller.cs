using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.Win32;

namespace NEXCORE;

public sealed class RemovableApp : INotifyPropertyChanged
{
    public string Name { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string Version { get; init; } = "";
    public string UninstallCommand { get; init; } = "";
    public string InstallLocation { get; init; } = "";
    public long EstimatedBytes { get; init; }
    internal string RegistryKey { get; init; } = "";
    internal string QuietUninstallCommand { get; init; } = "";

    private bool _selected;
    private string _status = "";
    private List<ResidualPath> _residuals = new();

    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public IReadOnlyList<ResidualPath> Residuals => _residuals;
    public string ResidualSummary => _residuals.Count == 0
        ? "Nenhum resquício encontrado nos locais analisados."
        : $"{_residuals.Count} local(is) · {Residuals.Sum(x => x.Bytes) / 1048576.0:N1} MB";

    internal void SetResiduals(IEnumerable<ResidualPath> paths)
    {
        _residuals = paths.ToList();
        OnChanged(nameof(Residuals));
        OnChanged(nameof(ResidualSummary));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnChanged(name);
    }
}

public sealed record ResidualPath(string Path, long Bytes, string Kind);

public static class AppUninstaller
{
    private const string UninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    public static List<RemovableApp> Installed()
    {
        var result = new List<RemovableApp>();
        foreach (var (hive, view) in new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Default),
        })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(UninstallPath);
                if (root == null) continue;
                foreach (string subName in root.GetSubKeyNames())
                {
                    using var key = root.OpenSubKey(subName);
                    if (key?.GetValue("DisplayName") is not string name || string.IsNullOrWhiteSpace(name)) continue;
                    string uninstall = (key.GetValue("UninstallString") as string ?? "").Trim();
                    string quiet = (key.GetValue("QuietUninstallString") as string ?? "").Trim();
                    if (uninstall.Length == 0 && quiet.Length == 0) continue;
                    string location = (key.GetValue("InstallLocation") as string ?? "").Trim().Trim('"');
                    result.Add(new RemovableApp
                    {
                        Name = name.Trim(),
                        Publisher = (key.GetValue("Publisher") as string ?? "").Trim(),
                        Version = (key.GetValue("DisplayVersion") as string ?? "").Trim(),
                        UninstallCommand = uninstall,
                        QuietUninstallCommand = quiet,
                        InstallLocation = location,
                        EstimatedBytes = EstimatedBytesFromRegistry(key.GetValue("EstimatedSize")),
                        RegistryKey = $"{hive}\\{view}\\{UninstallPath}\\{subName}",
                    });
                }
            }
            catch (Exception ex) { DiagnosticLog.Write("AppUninstaller.List", ex); }
        }

        return result
            .GroupBy(x => $"{x.Name}\u001f{x.Publisher}\u001f{x.Version}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.InstallLocation.Length).First())
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static async Task<(bool Ok, string Message)> UninstallAsync(RemovableApp app, CancellationToken ct)
    {
        string command = string.IsNullOrWhiteSpace(app.QuietUninstallCommand)
            ? app.UninstallCommand : app.QuietUninstallCommand;
        var (fileName, arguments) = SplitCommand(command);
        var psi = new ProcessStartInfo(fileName, arguments) { UseShellExecute = true };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o desinstalador.");
        await process.WaitForExitAsync(ct);
        return process.ExitCode == 0
            ? (true, "Desinstalação concluída.")
            : (false, $"O desinstalador terminou com código {process.ExitCode}.");
    }

    public static List<ResidualPath> FindResiduals(RemovableApp app)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddDirectory(roots, app.InstallLocation);
        string[] names = AppNameParts(app.Name).ToArray();
        foreach (string parent in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        })
        {
            if (!Directory.Exists(parent)) continue;
            foreach (string directory in SafeDirectories(parent))
            {
                string leaf = Path.GetFileName(directory);
                if (names.Any(part => leaf.Contains(part, StringComparison.OrdinalIgnoreCase)))
                    AddDirectory(roots, directory);
            }
        }

        return roots.Select(path => new ResidualPath(path, DirectorySize(path), "Pasta"))
                    .OrderByDescending(x => x.Bytes)
                    .ToList();
    }

    public static void DeleteResidual(ResidualPath residual)
    {
        if (!IsSafeResidual(residual.Path)) throw new InvalidOperationException("O caminho não passou na validação de segurança.");
        Directory.Delete(residual.Path, recursive: true);
    }

    private static IEnumerable<string> AppNameParts(string name) =>
        name.Split(new[] { ' ', '-', '_', '.', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x.Length >= 4 && !string.Equals(x, "update", StringComparison.OrdinalIgnoreCase))
            .Take(4);

    private static long EstimatedBytesFromRegistry(object? value)
    {
        try
        {
            long kilobytes = Convert.ToInt64(value ?? 0);
            return kilobytes > 0 && kilobytes <= long.MaxValue / 1024 ? kilobytes * 1024 : 0;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return 0;
        }
    }

    private static IEnumerable<string> SafeDirectories(string parent)
    {
        try { return Directory.EnumerateDirectories(parent, "*", SearchOption.TopDirectoryOnly).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private static void AddDirectory(HashSet<string> paths, string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            paths.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
    }

    private static long DirectorySize(string path)
    {
        long total = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return total;
    }

    private static bool IsSafeResidual(string path)
    {
        string full = Path.GetFullPath(path);
        string user = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        string programFiles = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        string programFilesX86 = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        string common = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        string local = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        string roaming = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        return !string.Equals(full, user, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(full, programFiles, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(full, programFilesX86, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(full, common, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(full, local, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(full, roaming, StringComparison.OrdinalIgnoreCase)
            && !full.EndsWith(Path.DirectorySeparatorChar);
    }

    private static (string FileName, string Arguments) SplitCommand(string command)
    {
        command = Environment.ExpandEnvironmentVariables(command.Trim());
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            if (end > 1) return (command[1..end], command[(end + 1)..].Trim());
        }
        int separator = command.IndexOf(' ');
        return separator < 0 ? (command, "") : (command[..separator], command[(separator + 1)..].Trim());
    }
}
