using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NEXCORE;

public sealed record BenchRun(DateTime Date, double? Multi, double? Single, bool Sustained, string Cpu)
{ public BenchmarkContext? Context { get; init; } }

/// <summary>Resultado dos benchmarks de jogo (Test = "gpu", "sim" ou "mem"). Score é usado para a barra do histórico.</summary>
public sealed record BenchRecord(DateTime Date, string Test, double Score, string Summary)
{ public BenchmarkContext? Context { get; init; } }

public sealed record BenchmarkContext(string Cpu, string Gpu, string Driver, string AppVersion,
    string WindowsVersion, int Threads, string Resolution, double DurationSeconds, Dictionary<string, string> Options)
{
    public long MemoryBytes { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string ComparisonKey => JsonSerializer.Serialize(new
    { Cpu, Gpu, Driver, AppVersion, WindowsVersion, Threads, MemoryBytes, Resolution, Options = Options.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray() });
    [System.Text.Json.Serialization.JsonIgnore]
    public string Description => $"{Cpu} · {Threads} threads · RAM {MemoryBytes / 1073741824.0:0.#} GB · {Gpu} · driver {Driver} · {Resolution} · {DurationSeconds:0.0} s · NEXCORE {AppVersion} · {WindowsVersion} · " +
        string.Join("; ", Options.Select(p => $"{p.Key}={p.Value}"));
}

/// <summary>Programa extra que abre antes do jogo (ex.: ferramenta de comando/monitoramento) e pode fechar junto com ele.</summary>
public sealed class HelperProgram
{
    public string Path { get; set; } = "";
    public string Args { get; set; } = "";
    public bool CloseWithGame { get; set; } = true;
    public string Display => System.IO.Path.GetFileName(Path) + (string.IsNullOrWhiteSpace(Args) ? "" : " " + Args);
}

/// <summary>Perfil de um jogo na aba NEXCORE JOGOS: desempenho, argumentos de gráficos e programas auxiliares.</summary>
public sealed class GameProfile
{
    public string Priority { get; set; } = "normal";   // normal | above | high
    public bool HighPerfPlan { get; set; }
    public bool GpuHigh { get; set; }
    public bool DisableFso { get; set; }
    public string? ExePath { get; set; }
    public string WinMode { get; set; } = "default";   // default | full | borderless | window
    public string Res { get; set; } = "default";       // default | 1280x720 | 1920x1080 | 2560x1440 | 3840x2160
    public int Hz { get; set; }                        // 0 = não definir
    public string Style { get; set; } = "source";      // source | unity | unreal
    public string Extra { get; set; } = "";
    public List<HelperProgram> Helpers { get; set; } = new();
}

/// <summary>Jogo adicionado à mão (qualquer .exe).</summary>
public sealed class CustomGame
{
    public string Name { get; set; } = "";
    public string Exe { get; set; } = "";
    public string Args { get; set; } = "";
}

/// <summary>Configurações em %AppData%\NEXCORE. A chave da API é cifrada com DPAPI (só este usuário do Windows a lê).</summary>
public sealed class AppSettings
{
    internal static string? StorageOverride { get; set; }
    internal static string? LegacyOverride { get; set; }
    public static string Folder => StorageOverride ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NEXCORE");
    private static string FilePath => Path.Combine(Folder, "settings.json");

    public string? HowManyFpsKeyProtected { get; set; }
    public string? SelectedGpuName { get; set; }
    public Dictionary<string, string?> FsoBackup { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // NEXCORE BENCH: últimos resultados (mais recente primeiro)
    public List<BenchRun> BenchRuns { get; set; } = new();
    public List<BenchRecord> BenchRecords { get; set; } = new();

    // NEXCORE OTIMIZA: valores originais dos ajustes do Windows antes da primeira alteração ("<ausente>" = não existia)
    public Dictionary<string, string?> TweakBackup { get; set; } = new();
    public Dictionary<string, string?> GpuPreferenceBackup { get; set; } = new();
    /// <summary>ComDB original (reservas de números COM) antes da primeira "liberação".</summary>
    public byte[]? ComDbBackup { get; set; }
    /// <summary>Programas de inicialização removidos, para "Restaurar removidos".</summary>
    public List<StartupBackup> StartupRemoved { get; set; } = new();

    // NEXCORE JOGOS
    public Dictionary<string, GameProfile> GameProfiles { get; set; } = new();
    public List<CustomGame> CustomGames { get; set; } = new();

    // Aparência
    public bool LightTheme { get; set; }

    // Overlay
    public string OverlayCorner { get; set; } = "TL";
    public double OverlayScale { get; set; } = 1.0;
    /// <summary>Itens escolhidos para o overlay (chaves de OverlayMetrics); null = padrão.</summary>
    public List<string>? OverlayItems { get; set; }

    [System.Text.Json.Serialization.JsonIgnore] // nunca gravar a chave em texto puro
    public string? HowManyFpsKey
    {
        get
        {
            if (string.IsNullOrEmpty(HowManyFpsKeyProtected)) return null;
            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(HowManyFpsKeyProtected), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return null; }
        }
        set
        {
            HowManyFpsKeyProtected = string.IsNullOrWhiteSpace(value)
                ? null
                : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), null, DataProtectionScope.CurrentUser));
        }
    }

    public static AppSettings Load()
    {
        string legacy = LegacyOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ResetExplode");
        var candidates = File.Exists(FilePath) || File.Exists(FilePath + ".bak")
            ? new[] { FilePath, FilePath + ".bak" }
            : new[] { Path.Combine(legacy, "settings.json"), Path.Combine(legacy, "settings.json.bak") };
        foreach (string candidate in candidates)
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(candidate))
                    ?? throw new JsonException("Configurações vazias.");
                settings.FsoBackup = new(settings.FsoBackup ?? new(), StringComparer.OrdinalIgnoreCase);
                settings.BenchRuns ??= new(); settings.BenchRecords ??= new();
                settings.TweakBackup ??= new(); settings.GpuPreferenceBackup ??= new();
                settings.StartupRemoved ??= new(); settings.GameProfiles ??= new(); settings.CustomGames ??= new();
                if (candidate != FilePath)
                {
                    Directory.CreateDirectory(Folder);
                    if (File.Exists(FilePath)) File.Move(FilePath, FilePath + ".corrupt-" + DateTime.UtcNow.Ticks);
                    settings.Save();
                    DiagnosticLog.Write("Configurações recuperadas ou migradas");
                }
                return settings;
            }
            catch (Exception ex) { DiagnosticLog.Write("Carregar configurações", ex); }
        }
        // Do not silently replace an unreadable backup of system changes.
        if (candidates.Any(File.Exists))
            throw new IOException("Não foi possível recuperar as configurações. Os arquivos foram preservados em " + Folder);
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        AtomicFile.Write(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal void BackupTweak(string key, string? value)
    {
        if (TweakBackup.ContainsKey(key)) return;
        TweakBackup[key] = value;
        try { Save(); }
        catch { TweakBackup.Remove(key); throw; }
    }
}
