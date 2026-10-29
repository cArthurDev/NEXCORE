using System.Windows;

namespace NEXCORE;

public partial class MainWindow
{
    private CancellationTokenSource? _tempCleanupCts;
    private Task<TempCleanupResult>? _tempCleanupTask;

    private static string CleanupSummary(TempCleanupResult result) =>
        $"{result.DeletedFiles:N0} arquivos excluídos · {result.RemovedFolders:N0} pastas vazias removidas · " +
        $"{result.FreedBytes / 1048576.0:N1} MB liberados · {result.Skipped:N0} itens ignorados (recentes, em uso, protegidos ou links).";

    private async void BtnCleanTemp_Click(object sender, RoutedEventArgs e) => await RunTempCleanupAsync(false);
    private async void BtnPreviewTemp_Click(object sender, RoutedEventArgs e) => await RunTempCleanupAsync(true);

    private async Task RunTempCleanupAsync(bool previewOnly)
    {
        if (_tempCleanupCts != null) { _tempCleanupCts.Cancel(); TempCleanupStatus.Text = "Cancelando…"; return; }
        var roots = new List<string>();
        if (ChkUserTemp.IsChecked == true) roots.Add(TempCleaner.UserTempPath);
        if (ChkWindowsTemp.IsChecked == true) roots.Add(TempCleaner.WindowsTempPath);
        if (roots.Count == 0) { TempCleanupStatus.Text = "Selecione pelo menos uma pasta."; return; }
        if (AnyBenchRunning || TestRunning || _closing) { TempCleanupStatus.Text = "Termine a operação em andamento antes de limpar."; return; }
        using var operation = _operations.TryStart("Analisar/limpar temporários");
        if (operation == null) { TempCleanupStatus.Text = "Termine a operação: " + _operations.CurrentName; return; }
        var minimumAge = ChkOldTempOnly.IsChecked == true ? TimeSpan.FromHours(48) : TimeSpan.Zero;
        _tempCleanupCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _tempCleanupCts.Token;
        BtnCleanTemp.Content = "CANCELAR";
        ChkUserTemp.IsEnabled = ChkWindowsTemp.IsEnabled = ChkOldTempOnly.IsEnabled = BtnPreviewTemp.IsEnabled = false;
        TempCleanupStatus.Text = "Analisando arquivos temporários…";
        var progress = new Progress<TempCleanupResult>(r => { if (!_closing) TempCleanupStatus.Text = CleanupSummary(r); });
        try
        {
            var preview = await (_tempCleanupTask = Task.Run(() => TempCleaner.Preview(roots, token, minimumAge)));
            if (preview.Cancelled || token.IsCancellationRequested) { TempCleanupStatus.Text = "Análise cancelada."; return; }
            string estimate = $"{preview.DeletedFiles:N0} arquivos elegíveis · até {preview.FreedBytes / 1048576.0:N1} MB recuperáveis (estimativa).";
            TempCleanupStatus.Text = estimate;
            if (previewOnly || preview.DeletedFiles == 0) return;
            if (MessageBox.Show(this, estimate + "\n\n" + string.Join("\n", roots) +
                (minimumAge > TimeSpan.Zero ? "\n\nSomente arquivos sem alterações nas últimas 48 horas." : "\n\nInclui arquivos recentes.") +
                "\nArquivos serão excluídos permanentemente. Itens em uso ou sem permissão serão ignorados. Continuar?",
                "Limpar temporários", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            token.ThrowIfCancellationRequested();
            var result = await (_tempCleanupTask = Task.Run(() => TempCleaner.Clean(roots, token, r => ((IProgress<TempCleanupResult>)progress).Report(r), minimumAge)));
            TempCleanupStatus.Text = (result.Cancelled ? "Limpeza cancelada. " : "Limpeza concluída. ") + CleanupSummary(result);
        }
        catch (OperationCanceledException) { TempCleanupStatus.Text = "Operação cancelada."; }
        catch (Exception ex) { DiagnosticLog.Write("Limpar temporários", ex); TempCleanupStatus.Text = "Não foi possível concluir a limpeza: " + ex.Message; }
        finally
        {
            _tempCleanupCts.Dispose(); _tempCleanupCts = null;
            BtnCleanTemp.Content = "LIMPAR TEMPORÁRIOS";
            ChkUserTemp.IsEnabled = ChkWindowsTemp.IsEnabled = ChkOldTempOnly.IsEnabled = BtnPreviewTemp.IsEnabled = true;
        }
    }
}
