using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace NEXCORE;

public partial class App : Application
{
    // Uma única instância: abrir o NEXCORE de novo só traz a janela que já está aberta para a frente.
    private static SingleInstanceGate? _single;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string title);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);

    protected override async void OnStartup(StartupEventArgs e)
    {
        // The elevated child must not read settings until its predecessor has
        // completed cancellation, restoration and its final writes.
        int handoff = Array.IndexOf(e.Args, "--handoff");
        if (handoff >= 0)
        {
            if (!SensorMonitor.IsElevated || handoff + 1 >= e.Args.Length ||
                !int.TryParse(e.Args[handoff + 1], out int parentId) || parentId == Environment.ProcessId)
            { Shutdown(1); return; }
            try
            {
                using var parent = System.Diagnostics.Process.GetProcessById(parentId);
                await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
            }
            catch (ArgumentException) { } // predecessor has already exited
            catch (Exception ex)
            {
                DiagnosticLog.Write("Transferir instância", ex);
                MessageBox.Show("A instância anterior ainda não encerrou. Aguarde e abra o NEXCORE novamente.", "NEXCORE");
                Shutdown(1); return;
            }
        }
        bool first;
        try
        {
            _single = SingleInstanceGate.TryAcquire();
            first = _single != null;
        }
        catch (UnauthorizedAccessException) { first = false; } // criado por outra instância com outro nível de permissão

        if (!first)
        {
            IntPtr h = FindWindow(null, "NEXCORE");
            if (h != IntPtr.Zero)
            {
                if (IsIconic(h)) ShowWindow(h, 9); // SW_RESTORE
                SetForegroundWindow(h);
            }
            Shutdown();
            return;
        }
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) => DiagnosticLog.Write("Exceção na interface", args.Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => DiagnosticLog.Write("Tarefa não observada", args.Exception);
        try
        {
            var window = new MainWindow();
            window.Show();
            int page = Array.IndexOf(e.Args, "--resume-page");
            if (page >= 0 && page + 1 < e.Args.Length) window.RestoreNavigation(e.Args[page + 1]);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Iniciar aplicativo", ex);
            MessageBox.Show("Não foi possível iniciar o NEXCORE: " + ex.Message, "NEXCORE", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _single?.Dispose(); } catch (ApplicationException) { }
        base.OnExit(e);
    }
}
