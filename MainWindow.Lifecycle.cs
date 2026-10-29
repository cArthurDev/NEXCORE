using System.ComponentModel;
using System.Windows;

namespace NEXCORE;

public partial class MainWindow
{
    private bool _closing, _shutdownComplete;
    private Task? _hardwareTask;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly OperationCoordinator _operations = new();
    private IDisposable? _stressOperation;
    private Task? _thermalWatchdog;
    private Task? _systemChangeTask;

    private async Task WaitForShutdownAsync(Task task, string description)
    {
        StatusText.Text = "Encerrando: " + description;
        await ShutdownWaiter.WaitAsync(task, TimeSpan.FromSeconds(30), () =>
        {
            DiagnosticLog.Write("Encerramento demorado", new TimeoutException(description));
            return MessageBox.Show(this, "Ainda aguardando: " + description +
                ".\n\nContinuar aguardando? Ao escolher Não, o NEXCORE será encerrado; operações externas já iniciadas podem continuar.",
                "Encerramento do NEXCORE", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        });
    }

    private string PendingShutdownOperation => _operations.CurrentName ??
        (_drvBusy ? "Instalação de driver" : _makerBusy ? "Drivers e portas seriais" :
         _appsBusy ? "Instalação de aplicativos" : _launchCts != null ? "Restaurar perfil do jogo" :
         _lightBusy ? "Iluminação" : "Finalizar testes");

    private void StartThermalWatchdog()
    {
        var token = _lifetime.Token;
        _thermalWatchdog = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(500, token);
                    if (_monitor.Paused || DateTime.UtcNow - _monitor.LastReadUtc > TimeSpan.FromSeconds(5))
                        _monitor.NotifySensorLoss();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        });
    }

    private void CheckStressTemperature(float? cpu, float? gpu)
    {
        if (!_stress.Running && !_gpuBurnOn) return;
        if (ThermalSafety.MustStop(_stress.Running, _gpuBurnOn, cpu, gpu, _tempLimit, _gpuTempLimit))
        {
            _stress.RequestStop();
            _burn.RequestStop();
            Dispatcher.BeginInvoke(new Action(() => StopStress("ABORTADO: limite térmico ou sensor indisponível")));
        }
    }

    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownComplete) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        IsEnabled = false;
        _timer.Stop(); _overlayTimer?.Stop(); _benchTimer?.Stop();
        StatusText.Text = "Encerrando operações…";
        _lifetime.Cancel();
        _benchCts?.Cancel(); _gameBenchCts?.Cancel(); _ocCts?.Cancel();
        _netCts?.Cancel(); _drvCts?.Cancel(); _gameFetch?.Cancel(); _launchCts?.Cancel();
        _rtBench?.Cancel(); _gpuBench?.Cancel(); _appsStop = true;
        _coverCts?.Cancel();
        _stress.RequestStop();
        try
        {
            await WaitForShutdownAsync(Task.Run(() => { _stress.Stop(); _burn.Stop(); }), "Parar testes de stress");
            _gpuBurnOn = false;
            _stressOperation?.Dispose(); _stressOperation = null;
            if (_systemChangeTask != null)
            {
                try { await WaitForShutdownAsync(_systemChangeTask, "Concluir ajuste do sistema"); }
                catch (Exception ex) when (ex is not ShutdownAbortedException) { DiagnosticLog.Write("Concluir ajuste antes de sair", ex); }
            }
            if (_tempCleanupTask != null)
            {
                try { await WaitForShutdownAsync(_tempCleanupTask, "Cancelar limpeza temporária"); } catch (Exception ex) when (ex is not ShutdownAbortedException) { DiagnosticLog.Write("Concluir limpeza temporária", ex); }
            }
            // Installations already handed to Windows are allowed to complete before closing.
            async Task DrainOperations()
            {
                while (BenchRunning || _gameBenchRunning || _ocRunning || _netRunning || _drvBusy || _makerBusy || _appsBusy || _lightBusy || _launchCts != null || _operations.IsBusy)
                {
                    StatusText.Text = "Encerrando: " + PendingShutdownOperation;
                    await Task.Delay(100);
                }
            }
            await WaitForShutdownAsync(DrainOperations(), PendingShutdownOperation);
            if (_hardwareTask != null) await WaitForShutdownAsync(_hardwareTask, "Finalizar detecção de hardware");
            if (_libraryScanTask != null)
            {
                try { await WaitForShutdownAsync(_libraryScanTask, "Finalizar busca de jogos"); } catch (OperationCanceledException ex) when (ex is not ShutdownAbortedException) { }
            }
            if (_coverTask != null) await WaitForShutdownAsync(_coverTask, "Cancelar carregamento de capas");
            if (_customCoverTask != null)
            {
                try { await WaitForShutdownAsync(_customCoverTask, "Gravar capa escolhida"); } catch (Exception ex) when (ex is not ShutdownAbortedException) { DiagnosticLog.Write("Concluir gravação da capa", ex); }
            }
            _coverCts?.Dispose();
            if (_thermalWatchdog != null) await _thermalWatchdog;
            _timer.Stop(); _overlayTimer?.Stop(); _benchTimer?.Stop();
            _overlay?.Close(); _toast?.Close();
            _frames.Dispose();
            UnregisterHotKey(new System.Windows.Interop.WindowInteropHelper(this).Handle, HotkeyId);
            ShutdownInteg();
            await WaitForShutdownAsync(_monitor.StopAsync(), "Liberar sensores de hardware");
            _fpsApi.Dispose();
            _lifetime.Dispose();
            _shutdownComplete = true;
            Close();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Encerrar aplicativo", ex);
            // Cancellation and disposal cannot be undone by re-enabling this window.
            _shutdownComplete = true;
            Application.Current.Shutdown();
        }
    }
}
