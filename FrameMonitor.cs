using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace NEXCORE;

public sealed record FrameStats(int ProcessId, string ProcessName, double Fps, double FrameTimeMs, double Low1Fps,
                                IReadOnlyList<double> RecentFrameTimes);

/// <summary>
/// Mede o FPS de qualquer processo sem injetar código, como o PresentMon: escuta via ETW os eventos
/// "Present" que o Windows emite a cada frame entregue por DXGI (DirectX 10/11/12) e Direct3D 9.
/// Jogos Vulkan/OpenGL só aparecem se apresentarem via DXGI (comum em drivers recentes, não garantido).
/// </summary>
public sealed class FrameMonitor : IDisposable
{
    private const string SessionName = "NEXCORE-Frames";
    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private const int DxgiPresentStart = 42, D3D9PresentStart = 1;
    private const double HistoryMs = 10_000; // janela para o 1% low

    private TraceEventSession? _session;
    private Thread? _thread;
    private readonly object _lock = new();
    private long _lastCleanup;
    private readonly Dictionary<(int Pid, ulong Chain), Queue<double>> _presents = new();
    private readonly Dictionary<(int Pid, ulong Chain), long> _lastSeen = new();

    public bool Running => _thread?.IsAlive == true;
    public string? Error { get; private set; }

    public void Start()
    {
        if (Running) return;
        try
        {
            // Opção Create derruba uma sessão antiga com o mesmo nome (ex.: app fechado à força).
            _session = new TraceEventSession(SessionName, TraceEventSessionOptions.Create) { StopOnDispose = true };
            _session.EnableProvider(DxgiProvider, TraceEventLevel.Informational, ulong.MaxValue, null);
            _session.EnableProvider(D3D9Provider, TraceEventLevel.Informational, ulong.MaxValue, null);
            _session.Source.AllEvents += OnEvent;
            var session = _session;
            _thread = new Thread(() =>
            {
                try { session.Source.Process(); }
                catch (Exception ex) { Error = ex.Message; }
            }) { IsBackground = true, Name = "ETW frames" };
            _thread.Start();
            Error = null;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            _session?.Dispose();
            _session = null;
        }
    }

    private void OnEvent(TraceEvent e)
    {
        int id = (int)e.ID;
        bool present = (e.ProviderGuid == DxgiProvider && id == DxgiPresentStart) ||
                       (e.ProviderGuid == D3D9Provider && id == D3D9PresentStart);
        if (!present) return;

        double t = e.TimeStampRelativeMSec;
        ulong chain = 0;
        try
        {
            string? payload = e.PayloadNames.FirstOrDefault(n => n.Contains("SwapChain", StringComparison.OrdinalIgnoreCase));
            if (payload != null)
            {
                object value = e.PayloadByName(payload);
                chain = value is IntPtr ptr ? unchecked((ulong)ptr.ToInt64()) : Convert.ToUInt64(value);
            }
        }
        catch (Exception ex) { DiagnosticLog.Write("Identificar apresentação ETW", ex); }
        var key = (e.ProcessID, chain);
        lock (_lock)
        {
            if (Stopwatch.GetElapsedTime(_lastCleanup).TotalSeconds > 5)
            {
                foreach (var expired in _lastSeen.Where(p => Stopwatch.GetElapsedTime(p.Value).TotalSeconds > 30).Select(p => p.Key).ToArray())
                { _lastSeen.Remove(expired); _presents.Remove(expired); }
                _lastCleanup = Stopwatch.GetTimestamp();
            }
            if (!_presents.TryGetValue(key, out var q)) _presents[key] = q = new Queue<double>();
            q.Enqueue(t);
            while (q.Count > 0 && q.Peek() < t - HistoryMs) q.Dequeue();
            _lastSeen[key] = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>Estatísticas do processo, ou null se ele não apresentou frames nos últimos 2 s.</summary>
    public FrameStats? GetStats(int pid)
    {
        double[] ts;
        lock (_lock)
        {
            var candidates = _presents.Where(p => p.Key.Pid == pid && Stopwatch.GetElapsedTime(_lastSeen[p.Key]).TotalSeconds <= 2)
                .OrderByDescending(p => p.Value.Count).ToList();
            if (candidates.Count == 0) return null;
            var q = candidates[0].Value;
            if (q.Count < 3) return null;
            ts = q.ToArray();
        }

        double last = ts[^1];
        var frameTimes = new List<double>(ts.Length);
        for (int i = 1; i < ts.Length; i++) frameTimes.Add(ts[i] - ts[i - 1]);

        // FPS do último segundo (pelo relógio dos eventos, imune ao atraso de entrega do ETW).
        int first = Array.FindIndex(ts, x => x >= last - 1000);
        double span = last - ts[first];
        int frames = ts.Length - 1 - first;
        double fps = span > 0 ? frames * 1000 / span : 0;

        // 1% low: FPS equivalente ao percentil 99 do frametime nos últimos 10 s.
        var sorted = frameTimes.OrderBy(x => x).ToArray();
        double p99 = sorted[(int)Math.Min(sorted.Length - 1, Math.Floor(sorted.Length * 0.99))];
        double low1 = p99 > 0 ? 1000 / p99 : 0;

        var recent = frameTimes.Skip(Math.Max(0, frameTimes.Count - 120)).ToList();
        return new FrameStats(pid, ProcessName(pid), fps, fps > 0 ? 1000 / fps : 0, low1, recent);
    }

    /// <summary>Estatísticas do programa em primeiro plano (o jogo que está na tela).</summary>
    public FrameStats? GetForegroundStats()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == 0 || pid == Environment.ProcessId ? null : GetStats((int)pid);
    }

    private static string ProcessName(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.ProcessName; }
        catch { return $"PID {pid}"; }
    }

    public void Dispose()
    {
        try { _session?.Source.StopProcessing(); } catch (Exception ex) { DiagnosticLog.Write("FrameMonitor", ex); }
        _session?.Dispose();
        _thread?.Join(3000);
        _session = null;
        lock (_lock) { _presents.Clear(); _lastSeen.Clear(); }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
}
