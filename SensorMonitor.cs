using System.Collections.ObjectModel;
using LibreHardwareMonitor.Hardware;
using System.Windows.Threading;

namespace NEXCORE;

/// <summary>Encapsula o LibreHardwareMonitorLib e mantém a lista de sensores atualizada.</summary>
public sealed class SensorMonitor : IDisposable
{
    private readonly Computer _computer;
    private readonly Dictionary<string, SensorRow> _index = new();
    private volatile bool _open;
    private readonly SemaphoreSlim _access = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task? _polling;
    private Task? _opening;
    private bool _disposed;
    private volatile bool _paused;
    public bool Paused { get => _paused; set => _paused = value; }
    private string? _selectedGpuName;
    public string? SelectedGpuName { get => Volatile.Read(ref _selectedGpuName); set => Volatile.Write(ref _selectedGpuName, value); }
    private List<Reading>? _pendingReadings;
    private int _publicationScheduled;
    public event Action<float?, float?>? TemperaturesRead;
    internal void NotifySensorLoss() => TemperaturesRead?.Invoke(null, null);
    public DateTime LastReadUtc { get; private set; }
    private sealed record Reading(string Id, string Hardware, string Name, SensorType Type, HardwareType HardwareType, float? Value, float? Min, float? Max);

    public ObservableCollection<SensorRow> Rows { get; } = new();
    public bool IsOpen => _open;

    public SensorMonitor()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsStorageEnabled = true,
            IsNetworkEnabled = true,
            IsControllerEnabled = true
        };
    }

    /// <summary>Detecta o hardware (leva de 0,5 a alguns segundos) fora da thread da janela. Até terminar, Refresh não faz nada.</summary>
    public Task OpenAsync() => _opening ??= Task.Run(async () =>
    {
        await _access.WaitAsync();
        try { if (!_disposed) { _computer.Open(); _open = true; } }
        finally { _access.Release(); }
    });

    public void StartPolling(Dispatcher dispatcher)
    {
        _polling ??= Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    if (!Paused) await RefreshAsync(dispatcher, _stop.Token, waitForPublication: false);
                    await Task.Delay(1000, _stop.Token);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception ex) { DiagnosticLog.Write("Coleta de sensores", ex); await Task.Delay(1000); }
            }
        });
    }

    public async Task RefreshAsync(Dispatcher dispatcher, CancellationToken ct = default, bool waitForPublication = true)
    {
        var readings = await Task.Run(async () =>
        {
            var list = new List<Reading>();
            await _access.WaitAsync(ct);
            try
            {
                if (_disposed || !_open) return list;
                void Collect(IHardware hw)
                {
                    ct.ThrowIfCancellationRequested();
                    hw.Update();
                    foreach (var s in hw.Sensors)
                        list.Add(new Reading(s.Identifier.ToString(), hw.Name, s.Name, s.SensorType, hw.HardwareType, s.Value, s.Min, s.Max));
                    foreach (var sub in hw.SubHardware) Collect(sub);
                }
                foreach (var hw in _computer.Hardware) Collect(hw);
            }
            finally { _access.Release(); }
            float? MaxTemp(HardwareType type) => list.Where(r => r.HardwareType == type && r.Type == SensorType.Temperature && r.Value > 0).Select(r => r.Value).DefaultIfEmpty(null).Max();
            LastReadUtc = DateTime.UtcNow;
            string? selectedGpu = SelectedGpuName;
            var gpuTemps = list.Where(r => (r.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel) && r.Hardware == selectedGpu && r.Type == SensorType.Temperature && r.Value > 0).ToList();
            float? gpuTemp = gpuTemps.FirstOrDefault(r => r.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase))?.Value
                ?? gpuTemps.FirstOrDefault(r => !r.Name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase) && !r.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase))?.Value;
            TemperaturesRead?.Invoke(MaxTemp(HardwareType.Cpu), gpuTemp);
            return list;
        }, ct);
        ct.ThrowIfCancellationRequested();
        Interlocked.Exchange(ref _pendingReadings, readings);
        if (Interlocked.Exchange(ref _publicationScheduled, 1) != 0) return;
        var publication = dispatcher.InvokeAsync(() =>
        {
            Interlocked.Exchange(ref _publicationScheduled, 0);
            if (_disposed || _stop.IsCancellationRequested) return;
            var latest = Interlocked.Exchange(ref _pendingReadings, null) ?? new();
            var seen = latest.Select(r => r.Id).ToHashSet();
            foreach (var absent in Rows.Where(row => !seen.Contains(row.Id))) absent.Update(null, null, null);
            foreach (var r in latest)
            {
                if (!_index.TryGetValue(r.Id, out var row))
                {
                    row = new SensorRow { Id = r.Id, Hardware = r.Hardware, Category = SensorRow.CategoryName(r.Type), Name = r.Name, Type = r.Type, HardwareType = r.HardwareType };
                    _index[r.Id] = row; Rows.Add(row);
                }
                row.Update(r.Value, r.Min, r.Max);
            }
        }, DispatcherPriority.Background);
        if (waitForPublication) await publication;
    }

    /// <summary>Temperatura principal da CPU: Package (Intel) / Tctl-Tdie (AMD), senão a maior entre os núcleos.</summary>
    public float? CpuTemperature
    {
        get
        {
            var temps = Rows.Where(r => r.HardwareType == HardwareType.Cpu
                                     && r.Type == SensorType.Temperature
                                     && r.RawValue is > 0).ToList();
            var main = temps.FirstOrDefault(r => r.Name.Contains("Package") || r.Name.Contains("Tctl") || r.Name.Contains("Tdie"));
            return main?.RawValue ?? temps.Max(r => r.RawValue);
        }
    }

    /// <summary>Carga total da CPU em %.</summary>
    public float? CpuLoad => Rows.FirstOrDefault(r => r.HardwareType == HardwareType.Cpu
                                                   && r.Type == SensorType.Load
                                                   && r.Name == "CPU Total")?.RawValue;

    /// <summary>Consumo do pacote da CPU em W.</summary>
    public float? CpuPower => Rows.FirstOrDefault(r => r.HardwareType == HardwareType.Cpu
                                                    && r.Type == SensorType.Power
                                                    && r.Name.Contains("Package"))?.RawValue;

    /// <summary>
    /// Driver PawnIO (necessário para temperatura/consumo da CPU via MSR/SMN).
    /// A biblioteca procura exatamente esta chave de desinstalação.
    /// </summary>
    public static bool IsPawnIOInstalled
    {
        get
        {
            using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine,
                                                                     Microsoft.Win32.RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
            return key != null;
        }
    }

    public static bool IsElevated =>
        new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

    public enum CpuSensorProblem { None, NotElevated, PawnIOMissing, ReadFailed }

    /// <summary>Por que a temperatura/consumo da CPU não aparecem (o motivo mais provável primeiro).</summary>
    public CpuSensorProblem DiagnoseCpuSensors()
    {
        if (CpuTemperature != null) return CpuSensorProblem.None;
        if (!IsElevated) return CpuSensorProblem.NotElevated;
        if (!IsPawnIOInstalled) return CpuSensorProblem.PawnIOMissing;
        return CpuSensorProblem.ReadFailed;
    }

    public static string Explain(CpuSensorProblem p) => p switch
    {
        CpuSensorProblem.NotElevated =>
            "O NEXCORE não está rodando como administrador, e o Windows só libera os sensores internos da CPU " +
            "nesse modo. Feche e abra de novo aceitando o pedido de administrador.",
        CpuSensorProblem.PawnIOMissing =>
            "Falta o driver PawnIO, que lê a temperatura e o consumo da CPU. Ele é gratuito, assinado e usado também " +
            "pelo LibreHardwareMonitor e pelo FanControl. Depois de instalar, feche e abra o NEXCORE.",
        CpuSensorProblem.ReadFailed =>
            "O PawnIO está instalado, mas a leitura falhou. Se acabou de instalar, reinicie o PC. Se continuar, " +
            "confira se o antivírus não bloqueou o driver ou se a CPU é muito antiga/recente para a biblioteca de sensores.",
        _ => ""
    };

    public void Dispose()
    {
        _stop.Cancel();
        _access.Wait();
        try { _disposed = true; if (_open) _computer.Close(); _open = false; }
        finally { _access.Release(); }
    }

    public async Task StopAsync()
    {
        _stop.Cancel();
        if (_opening != null) await _opening;
        if (_polling != null) await _polling;
        await Task.Run(Dispose);
    }
}
