using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace NEXCORE;

/// <summary>Aba NEXCORE JOGOS: lista os jogos instalados, guarda um perfil por jogo e inicia aplicando o perfil.</summary>
public partial class MainWindow
{
    private List<LibGame> _libGames = new();
    private LibGame? _lg;
    private GameProfile? _lp;
    private bool _lLoading, _launcherInited;
    private CancellationTokenSource? _launchCts;
    private readonly Dictionary<string, string?> _exeGuess = new();
    private CancellationTokenSource? _coverCts;
    private Task? _coverTask;
    private Task<List<LibGame>>? _libraryScanTask;
    private Task<System.Windows.Media.Imaging.BitmapSource>? _customCoverTask;
    private bool _libraryScanning;
    private readonly HashSet<LibGame> _visibleCoverGames = new();
    private readonly Dictionary<LibGame, Task> _coverLoads = new();
    private readonly HashSet<LibGame> _missingCovers = new();
    private readonly SemaphoreSlim _coverSlots = new(4);

    private static readonly (string Label, string Token, string? Hint)[] ExtraPresets =
    {
        ("Pular intro (-novid)", "-novid", null), ("Prioridade alta (-high)", "-high", null), ("Sem limite de FPS (+fps_max 0)", "+fps_max 0", null),
        ("Sem joystick (-nojoy)", "-nojoy", null), ("Abrir console (-console)", "-console", null),
        ("Programas de terceiros (-allow_third_party_software)", "-allow_third_party_software",
         "CS2: libera programas que o modo confiável bloqueia, como a captura de jogo do OBS, ReShade e alguns overlays.\n" +
         "Atenção: a Valve diz que usar esta opção pode reduzir o seu Fator de Confiança (Trust Factor) no matchmaking."),
    };

    private void InitLauncher()
    {
        if (_launcherInited) return;
        _launcherInited = true;
        foreach (var (label, token, hint) in ExtraPresets)
        {
            var b = new Button
            {
                Content = label, Tag = token, Style = (Style)FindResource("OutlineButton"),
                Padding = new Thickness(10, 4, 10, 4), FontSize = 11, Margin = new Thickness(0, 0, 6, 6),
                ToolTip = hint,
            };
            b.Click += (_, _) =>
            {
                string t = (string)b.Tag;
                if (!LExtra.Text.Contains(t, StringComparison.OrdinalIgnoreCase))
                    LExtra.Text = (LExtra.Text.Trim() + " " + t).Trim();
            };
            LExtraPresets.Children.Add(b);
        }
        _ = ScanGamesAsync();
    }

    // ---------------- Lista ----------------

    private async Task ScanGamesAsync()
    {
        if (_libraryScanning || _closing) return;
        _libraryScanning = true;
        BtnScanGames.IsEnabled = false;
        LGameCount.Text = "Procurando seus jogos…";
        try
        {
            var custom = _settings.CustomGames.ToList();
            _coverCts?.Cancel();
            if (_coverTask != null) await _coverTask;
            _coverCts?.Dispose();
            _coverCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _missingCovers.Clear();
            _libGames = await (_libraryScanTask = Task.Run(() => GameLibrary.Scan(custom), _lifetime.Token));
            if (_closing) return;
            ApplyGameFilter();
            _ = Task.Run(GameCoverService.PruneCache);
            var byStore = _libGames.GroupBy(g => g.Store).Select(x => $"{x.Key} {x.Count()}");
            LGameCount.Text = _libGames.Count == 0
                ? "Nenhum jogo encontrado. Use + ADICIONAR .EXE para incluir um à mão."
                : $"{_libGames.Count} jogos · {string.Join(" · ", byStore)}";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { DiagnosticLog.Write("Listar jogos", ex); LGameCount.Text = "Falhou: " + ex.Message; }
        finally { _libraryScanning = false; BtnScanGames.IsEnabled = true; }
    }

    private void LibraryScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange != 0 || e.ViewportHeightChange != 0)
            Dispatcher.BeginInvoke(new Action(RefreshVisibleCovers), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void LibraryCardLoaded(object sender, RoutedEventArgs e) => RefreshVisibleCovers();
    private void LibraryCardUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: LibGame game })
        {
            _visibleCoverGames.Remove(game);
            if (_lg != game) game.Cover = null;
        }
    }

    private void RefreshVisibleCovers()
    {
        if (_closing || _coverCts == null || _coverCts.IsCancellationRequested) return;
        foreach (var stale in _visibleCoverGames.ToArray())
            if (!_libGames.Contains(stale) || LGameList.ItemContainerGenerator.ContainerFromItem(stale) == null)
            { _visibleCoverGames.Remove(stale); if (_lg != stale) stale.Cover = null; }
        foreach (var item in RealizedCoverGames(LGameList))
            {
                _visibleCoverGames.Add(item);
                if (item.Cover != null || _coverLoads.ContainsKey(item) || _missingCovers.Contains(item)) continue;
                var task = LoadVisibleCoverAsync(item, _coverCts.Token);
                _coverLoads[item] = task;
                _coverTask = Task.WhenAll(_coverLoads.Values.ToArray());
            }
    }

    private static IEnumerable<LibGame> RealizedCoverGames(DependencyObject node)
    {
        if (node is ListBoxItem { DataContext: LibGame game }) { yield return game; yield break; }
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (var child in RealizedCoverGames(System.Windows.Media.VisualTreeHelper.GetChild(node, i))) yield return child;
    }

    private async Task LoadVisibleCoverAsync(LibGame game, CancellationToken token)
    {
        // Yield so the caller registers the task before its finally removes it.
        await Task.Yield();
        bool entered = false;
        try
        {
            await _coverSlots.WaitAsync(token); entered = true;
            if (!_visibleCoverGames.Contains(game) && _lg != game) return;
            int revision = game.CoverRevision;
            var cover = await Task.Run(() => GameCoverService.LoadAsync(game, token), token);
            if (_closing || token.IsCancellationRequested || revision != game.CoverRevision ||
                (!_visibleCoverGames.Contains(game) && _lg != game)) return;
            if (cover == null) _missingCovers.Add(game);
            game.Cover = cover;
            if (_lg == game) LSelectedCover.Source = cover;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { DiagnosticLog.Write("Carregar capa visível", ex); }
        finally { if (entered) _coverSlots.Release(); _coverLoads.Remove(game); }
    }

    private void BtnBackLibrary_Click(object sender, RoutedEventArgs e)
    {
        LGameList.SelectedItem = null;
        LDetail.Visibility = Visibility.Collapsed;
        LLibrary.Visibility = Visibility.Visible;
        LGameSearch.Focus();
    }

    private async void BtnChooseCover_Click(object sender, RoutedEventArgs e)
    {
        var game = _lg;
        if (game == null) return;
        var dialog = new OpenFileDialog { Title = "Escolha a capa do jogo", Filter = "Imagens|*.jpg;*.jpeg;*.png;*.webp;*.bmp" };
        if (dialog.ShowDialog(this) != true) return;
        game.CoverRevision++;
        BtnChooseCover.IsEnabled = false;
        try
        {
            var image = await (_customCoverTask = Task.Run(() => GameCoverService.SaveCustom(game, dialog.FileName)));
            game.Cover = image;
            if (_lg == game) { LSelectedCover.Source = image; LStatus.Text = "Capa salva."; }
        }
        catch (Exception ex) { DiagnosticLog.Write("Escolher capa", ex); if (_lg == game) LStatus.Text = "Não foi possível usar esta imagem. Escolha um arquivo JPG ou PNG de até 5 MB."; }
        finally { BtnChooseCover.IsEnabled = true; }
    }

    private void ApplyGameFilter()
    {
        string q = LGameSearch.Text.Trim();
        string? keep = _lg?.Key;
        var list = _libGames.Where(g => q.Length == 0 || g.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
        LGameList.ItemsSource = list;
        LLibraryEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LLibraryEmpty.Text = q.Length == 0 ? "Nenhum jogo encontrado. Adicione um .EXE para começar." : "Nenhum jogo corresponde à busca.";
        if (keep != null) LGameList.SelectedItem = list.FirstOrDefault(g => g.Key == keep);
    }

    private void LGameSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (LGameList == null) return;
        ApplyGameFilter();
    }

    private async void BtnScanGames_Click(object sender, RoutedEventArgs e) => await ScanGamesAsync();

    private async void BtnAddExe_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Programas (*.exe)|*.exe", Title = "Escolha o .exe do jogo" };
        if (dlg.ShowDialog(this) != true) return;
        if (!_settings.CustomGames.Any(c => c.Exe.Equals(dlg.FileName, StringComparison.OrdinalIgnoreCase)))
        {
            _settings.CustomGames.Add(new CustomGame { Name = Path.GetFileNameWithoutExtension(dlg.FileName), Exe = dlg.FileName });
            _settings.Save();
        }
        await ScanGamesAsync();
        LGameList.SelectedItem = (LGameList.ItemsSource as IEnumerable<LibGame>)?.FirstOrDefault(g => g.Store == "Manual" && g.Id == dlg.FileName);
    }

    // ---------------- Perfil ----------------

    private static void SetChip(Panel panel, string tag)
    {
        var chips = panel.Children.OfType<RadioButton>().ToList();
        var hit = chips.FirstOrDefault(c => (string)c.Tag == tag) ?? chips.FirstOrDefault();
        if (hit != null) hit.IsChecked = true;
    }

    private async void LGameList_Changed(object sender, SelectionChangedEventArgs e)
    {
        var previousGame = _lg;
        _lg = LGameList.SelectedItem as LibGame;
        if (previousGame != null && previousGame != _lg && !_visibleCoverGames.Contains(previousGame)) previousGame.Cover = null;
        if (_lg == null) { LDetail.Visibility = Visibility.Collapsed; LLibrary.Visibility = Visibility.Visible; LEmpty.Visibility = Visibility.Collapsed; _lp = null; return; }
        LDetail.Visibility = Visibility.Visible;
        LLibrary.Visibility = Visibility.Collapsed;
        LDetail.ScrollToTop();
        LSelectedCover.Source = _lg.Cover;
        LEmpty.Visibility = Visibility.Collapsed;

        var g = _lg;
        if (!_settings.GameProfiles.TryGetValue(g.Key, out var p)) { p = new GameProfile(); _settings.GameProfiles[g.Key] = p; }
        _lLoading = true;
        _lp = p;
        LTitle.Text = g.Name;
        LSub.Text = $"{g.Store} · {g.InstallDir}";
        SetChip(LPriorityPanel, p.Priority);
        LChkPlan.IsChecked = p.HighPerfPlan;
        LChkGpu.IsChecked = p.GpuHigh;
        LChkFso.IsChecked = p.DisableFso;
        SetChip(LModePanel, p.WinMode);
        SetChip(LResPanel, p.Res);
        SetChip(LHzPanel, p.Hz.ToString());
        SetChip(LStylePanel, p.Style);
        LExtra.Text = p.Extra;
        LHelpers.ItemsSource = null;
        LHelpers.ItemsSource = p.Helpers;
        LSteamBox.Visibility = g.IsSteam ? Visibility.Visible : Visibility.Collapsed;
        LSteamNow.Text = g.IsSteam ? "Opções atuais na Steam: " + (SteamInfo.GetLaunchOptions(g.Id) is { Length: > 0 } cur ? cur : "(nenhuma)") : "";
        LGfxNote.Text = g.IsSteam
            ? "Os argumentos só passam a valer na Steam depois de APLICAR NAS OPÇÕES DA STEAM (a Steam é fechada nessa hora). Depois, o JOGAR abre pela Steam normalmente."
            : g.LaunchUri != null
                ? "Esta loja abre o jogo por um atalho que não aceita argumentos: use as opções de inicialização da própria loja. Desempenho e programas auxiliares funcionam normalmente."
                : "Os argumentos são enviados direto ao .exe quando você clica em JOGAR.";
        LStatus.Text = "";
        _lLoading = false;
        UpdateLauncherPreview();
        await ShowExeAsync(g, p);
    }

    private async Task ShowExeAsync(LibGame g, GameProfile p)
    {
        string? exe = p.ExePath ?? g.ExePath;
        if (exe == null)
        {
            LExeText.Text = "Procurando o executável…";
            if (!_exeGuess.TryGetValue(g.Key, out exe))
            {
                string dir = g.InstallDir, name = g.Name;
                exe = await Task.Run(() => GameLauncher.FindMainExe(dir, name));
                _exeGuess[g.Key] = exe;
            }
            if (_lg != g) return; // o usuário já trocou de jogo
            LExeText.Text = exe != null ? $"Executável (detectado, confirme): {exe}" : "Executável não identificado. Ele é descoberto ao jogar, ou escolha manualmente.";
        }
        else LExeText.Text = "Executável: " + exe;
    }

    private void SaveProfileFromControls()
    {
        if (_lLoading || _lp == null || LPreview == null) return;
        _lp.Priority = CheckedTag(LPriorityPanel) ?? "normal";
        _lp.HighPerfPlan = LChkPlan.IsChecked == true;
        _lp.GpuHigh = LChkGpu.IsChecked == true;
        _lp.DisableFso = LChkFso.IsChecked == true;
        _lp.WinMode = CheckedTag(LModePanel) ?? "default";
        _lp.Res = CheckedTag(LResPanel) ?? "default";
        _lp.Hz = int.TryParse(CheckedTag(LHzPanel), out int hz) ? hz : 0;
        _lp.Style = CheckedTag(LStylePanel) ?? "source";
        _lp.Extra = LExtra.Text.Trim();
        try { _settings.Save(); } catch (Exception ex) { DiagnosticLog.Write("MainWindow.Launcher", ex); }
        UpdateLauncherPreview();
    }

    private void LProfile_Changed(object sender, RoutedEventArgs e) => SaveProfileFromControls();
    private void LExtra_Changed(object sender, TextChangedEventArgs e) => SaveProfileFromControls();

    private void UpdateLauncherPreview()
    {
        if (_lp == null || LPreview == null) return;
        string a = GameLauncher.BuildArgs(_lp);
        LPreview.Text = a.Length > 0 ? a : "(nenhum argumento)";
    }

    private void BtnRecommended_Click(object sender, RoutedEventArgs e)
    {
        if (_lp == null) return;
        _lLoading = true;
        SetChip(LPriorityPanel, "high");
        LChkPlan.IsChecked = true;
        LChkGpu.IsChecked = true;
        LChkFso.IsChecked = true;
        _lLoading = false;
        SaveProfileFromControls();
        LStatus.Text = "Perfil para mais FPS ligado: prioridade alta, Alto desempenho, GPU dedicada e sem otimizações de tela cheia.";
    }

    private async void BtnPickExe_Click(object sender, RoutedEventArgs e)
    {
        if (_lg == null || _lp == null) return;
        var dlg = new OpenFileDialog
        {
            Filter = "Programas (*.exe)|*.exe", Title = "Escolha o .exe do jogo",
            InitialDirectory = Directory.Exists(_lg.InstallDir) ? _lg.InstallDir : "",
        };
        if (dlg.ShowDialog(this) != true) return;
        _lp.ExePath = dlg.FileName;
        try { _settings.Save(); } catch (Exception ex) { DiagnosticLog.Write("MainWindow.Launcher", ex); }
        await ShowExeAsync(_lg, _lp);
    }

    // ---------------- Programas auxiliares ----------------

    private void BtnAddHelper_Click(object sender, RoutedEventArgs e)
    {
        if (_lp == null) return;
        var dlg = new OpenFileDialog { Filter = "Programas e scripts|*.exe;*.bat;*.cmd;*.lnk", Title = "Programa para abrir junto com o jogo" };
        if (dlg.ShowDialog(this) != true) return;
        _lp.Helpers.Add(new HelperProgram { Path = dlg.FileName, CloseWithGame = LHelperClose.IsChecked == true });
        try { _settings.Save(); } catch (Exception ex) { DiagnosticLog.Write("MainWindow.Launcher", ex); }
        LHelpers.ItemsSource = null;
        LHelpers.ItemsSource = _lp.Helpers;
    }

    private void BtnRemoveHelper_Click(object sender, RoutedEventArgs e)
    {
        if (_lp == null || ((FrameworkElement)sender).Tag is not HelperProgram h) return;
        _lp.Helpers.Remove(h);
        try { _settings.Save(); } catch (Exception ex) { DiagnosticLog.Write("MainWindow.Launcher", ex); }
        LHelpers.ItemsSource = null;
        LHelpers.ItemsSource = _lp.Helpers;
    }

    // ---------------- Steam: opções de inicialização ----------------

    private async void BtnSteamApply_Click(object sender, RoutedEventArgs e)
    {
        var g = _lg;
        if (g == null || !g.IsSteam || _lp == null) return;
        string opts = GameLauncher.BuildArgs(_lp);
        BtnSteamApply.IsEnabled = false;
        try
        {
            if (SteamInfo.IsRunning)
            {
                var ask = MessageBox.Show(this,
                    "A Steam precisa ser fechada para gravar as opções de inicialização (senão ela as sobrescreve ao sair).\n\n" +
                    "Fechar a Steam agora? Jogos abertos serão encerrados. Feito um backup do arquivo antes de alterar.",
                    "NEXCORE JOGOS · Steam", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (ask != MessageBoxResult.Yes) return;
                LStatus.Text = "Fechando a Steam…";
                if (!await SteamInfo.ShutdownAsync()) { LStatus.Text = "A Steam não fechou. Feche-a manualmente e tente de novo."; return; }
            }
            string? err = await Task.Run(() => SteamInfo.SetLaunchOptions(g.Id, opts));
            LStatus.Text = err ?? (opts.Length > 0 ? $"Opções gravadas na Steam: {opts}. Clique em JOGAR (a Steam abre sozinha)." : "Opções da Steam limpas para este jogo.");
            LSteamNow.Text = "Opções atuais na Steam: " + (SteamInfo.GetLaunchOptions(g.Id) is { Length: > 0 } cur ? cur : "(nenhuma)");
        }
        finally { BtnSteamApply.IsEnabled = true; }
    }

    // ---------------- Jogar ----------------

    private async void BtnPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_launchCts != null) { _launchCts.Cancel(); return; }
        if (AnyBenchRunning || TestRunning) { LStatus.Text = "Termine a operação em andamento antes de iniciar o jogo."; return; }
        var g = _lg;
        var p = _lp;
        if (g == null || p == null) return;

        _launchCts = new CancellationTokenSource();
        BtnPlay.Content = "PARAR ACOMPANHAMENTO";
        try
        {
            await GameLauncher.RunAsync(g, p,
                t => Dispatcher.BeginInvoke(new Action(() => LStatus.Text = t)),
                exe => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!string.Equals(p.ExePath, exe, StringComparison.OrdinalIgnoreCase))
                    {
                        p.ExePath = exe;
                        try { _settings.Save(); } catch (Exception ex) { DiagnosticLog.Write("MainWindow.Launcher", ex); }
                        if (_lg == g) LExeText.Text = "Executável: " + exe;
                    }
                })),
                _launchCts.Token, _settings);
        }
        finally
        {
            _launchCts?.Dispose();
            _launchCts = null;
            BtnPlay.Content = "JOGAR";
        }
    }
}
