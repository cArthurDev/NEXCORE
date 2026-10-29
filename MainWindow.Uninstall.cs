using System.Windows;

namespace NEXCORE;

public partial class MainWindow
{
    private List<RemovableApp>? _removableApps;
    private bool _uninstallBusy;

    private void InitUninstaller()
    {
        if (_uninstallBusy) return;
        try
        {
            _removableApps = AppUninstaller.Installed();
            UninstallList.ItemsSource = _removableApps;
            UninstallStatus.Text = $"{_removableApps.Count} aplicativos encontrados no Windows.";
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Listar aplicativos", ex);
            UninstallStatus.Text = $"Não foi possível listar os aplicativos: {ex.Message}";
        }
    }

    private void BtnUninstallRefresh_Click(object sender, RoutedEventArgs e) => InitUninstaller();

    private async void BtnUninstallSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_removableApps == null || _uninstallBusy) return;
        if (AnyBenchRunning || TestRunning || _closing)
        {
            UninstallStatus.Text = "Termine a operação em andamento antes de desinstalar.";
            return;
        }

        var selected = _removableApps.Where(x => x.Selected).ToList();
        if (selected.Count == 0)
        {
            UninstallStatus.Text = "Marque pelo menos um aplicativo.";
            return;
        }

        var names = string.Join("\n", selected.Take(12).Select(x => $"  • {x.Name}"));
        if (selected.Count > 12) names += $"\n  … e mais {selected.Count - 12}";
        if (MessageBox.Show(this,
            $"O NEXCORE vai abrir o desinstalador oficial de {selected.Count} aplicativo(s):\n\n{names}\n\n" +
            "Depois, ele poderá localizar pastas relacionadas em AppData, ProgramData, Menu Iniciar e na pasta de instalação. " +
            "Esses locais podem conter saves, perfis, projetos e configurações pessoais. " +
            "Você revisará a lista de resíduos antes da exclusão. Continuar?",
            "Desinstalar aplicativos", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        using var operation = _operations.TryStart("Desinstalar aplicativos");
        if (operation == null) return;
        _uninstallBusy = true;
        BtnUninstallSelected.Content = "PROCESSANDO…";
        BtnUninstallSelected.IsEnabled = false;
        try
        {
            foreach (var app in selected)
            {
                app.Status = "Aguardando o desinstalador…";
                try
                {
                    var result = await AppUninstaller.UninstallAsync(app, _lifetime.Token);
                    app.Status = result.Message;
                    if (result.Ok)
                    {
                        await Task.Delay(500, _lifetime.Token);
                        app.SetResiduals(AppUninstaller.FindResiduals(app));
                    }
                }
                catch (OperationCanceledException) { app.Status = "Cancelado."; break; }
                catch (Exception ex)
                {
                    DiagnosticLog.Write($"Desinstalar {app.Name}", ex);
                    app.Status = $"Falhou: {ex.Message}";
                }
            }

            var withResiduals = selected.Where(x => x.Residuals.Count > 0).ToList();
            if (withResiduals.Count == 0)
            {
                UninstallStatus.Text = "Desinstalação concluída. Nenhum resquício foi encontrado nos locais analisados.";
                InitUninstaller();
                return;
            }

            UninstallResidualList.ItemsSource = withResiduals.SelectMany(x => x.Residuals.Select(r => new ResidualRow(x, r))).ToList();
            UninstallResidualPanel.Visibility = Visibility.Visible;
            UninstallStatus.Text = "Revise os resíduos abaixo. Nada será excluído até você marcar e confirmar.";
        }
        finally
        {
            _uninstallBusy = false;
            BtnUninstallSelected.Content = "DESINSTALAR SELECIONADOS";
            BtnUninstallSelected.IsEnabled = true;
        }
    }

    private void BtnDeleteResiduals_Click(object sender, RoutedEventArgs e)
    {
        if (_uninstallBusy || UninstallResidualList.ItemsSource is not IEnumerable<ResidualRow> rows) return;
        var selected = rows.Where(x => x.Selected).ToList();
        if (selected.Count == 0) { UninstallStatus.Text = "Marque os resíduos que deseja excluir."; return; }
        long bytes = selected.Sum(x => x.Residual.Bytes);
        if (MessageBox.Show(this,
            $"Excluir permanentemente {selected.Count} local(is), liberando até {bytes / 1048576.0:N1} MB?\n\n" +
            string.Join("\n", selected.Take(10).Select(x => $"  • {x.Residual.Path}")) +
            (selected.Count > 10 ? $"\n  … e mais {selected.Count - 10}" : "") +
            "\n\nEssa ação não pode ser desfeita.",
            "Excluir resíduos", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        _uninstallBusy = true;
        BtnDeleteResiduals.IsEnabled = false;
        int deleted = 0;
        var failed = new List<string>();
        try
        {
            foreach (var row in selected)
            {
                try { AppUninstaller.DeleteResidual(row.Residual); row.Status = "Excluído."; deleted++; }
                catch (Exception ex) { row.Status = $"Não excluído: {ex.Message}"; failed.Add(row.Residual.Path); }
            }
            UninstallStatus.Text = $"{deleted} resíduo(s) excluído(s)." +
                (failed.Count > 0 ? $" Não excluídos: {failed.Count} (em uso ou sem permissão)." : "");
            UninstallResidualList.ItemsSource = rows.Where(x => x.Status != "Excluído.").ToList();
            if (!((IEnumerable<ResidualRow>)UninstallResidualList.ItemsSource).Any())
                UninstallResidualPanel.Visibility = Visibility.Collapsed;
            InitUninstaller();
        }
        finally
        {
            _uninstallBusy = false;
            BtnDeleteResiduals.IsEnabled = true;
        }
    }

    private sealed class ResidualRow
    {
        public ResidualRow(RemovableApp app, ResidualPath residual) { App = app; Residual = residual; }
        public RemovableApp App { get; }
        public ResidualPath Residual { get; }
        public bool Selected { get; set; } = true;
        public string Status { get; set; } = "";
        public string Size => $"{Residual.Bytes / 1048576.0:N1} MB";
    }
}
