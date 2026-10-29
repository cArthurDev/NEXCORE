using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NEXCORE;

public partial class MainWindow
{
    internal void RestoreNavigation(string page)
    {
        void Visit(DependencyObject node)
        {
            if (node is RadioButton button && button.Tag as string == page)
            { button.IsChecked = true; return; }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Visit(VisualTreeHelper.GetChild(node, i));
        }
        Visit(this);
    }

    private void BtnRestoreFso_Click(object sender, RoutedEventArgs e)
    {
        if (_lp == null || _lg == null || _closing) return;
        if (AnyBenchRunning || TestRunning) { LStatus.Text = "Termine a operação em andamento antes de restaurar."; return; }
        using var operation = _operations.TryStart("Restaurar tela cheia");
        if (operation == null || _launchCts != null) { LStatus.Text = "Termine a operação em andamento antes de restaurar."; return; }
        string? exe = _lp.ExePath ?? _lg.ExePath;
        if (exe == null || !_settings.FsoBackup.ContainsKey(exe))
        { LStatus.Text = "Não há alteração de tela cheia guardada para este executável."; return; }
        try
        {
            GameLauncher.SetDisableFso(exe, false, _settings);
            _lp.DisableFso = false; LChkFso.IsChecked = false; _settings.Save();
            LStatus.Text = "Configuração original de tela cheia restaurada.";
        }
        catch (Exception ex) { DiagnosticLog.Write("Restaurar tela cheia", ex); LStatus.Text = "Falhou: " + ex.Message; }
    }

    private BenchmarkContext CaptureBenchmark(string resolution = "Não se aplica", double seconds = 0,
        Dictionary<string, string>? options = null) => new(
            _cpuName, SelectedGpu?.Name ?? "Não disponível",
            SelectedGpu?.Sections.SelectMany(s => s.Children).FirstOrDefault(n => n.Header == "Versão do driver")?.Value ?? "Não disponível",
            typeof(App).Assembly.GetName().Version?.ToString() ?? "Não disponível",
            Environment.OSVersion.VersionString, Environment.ProcessorCount, resolution, seconds, options ?? new())
            { MemoryBytes = (long)Optimizer.MemoryStatus().Total };
}
