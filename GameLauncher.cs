using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace NEXCORE;

/// <summary>
/// Aplica o perfil de um jogo e o inicia. Tudo é reversível: o plano de energia volta ao anterior, os programas auxiliares fecham e
/// as preferências por jogo (GPU, tela cheia) ficam no registro do usuário só para aquele .exe.
/// </summary>
public static class GameLauncher
{
    private const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

    // ---------------- Argumentos de gráficos ----------------

    /// <summary>Monta a linha de argumentos do perfil (modo de janela, resolução, taxa e extras) no estilo da engine escolhida.</summary>
    public static string BuildArgs(GameProfile p)
    {
        var a = new List<string>();
        int w = 0, h = 0;
        var m = Regex.Match(p.Res ?? "", @"^(\d+)x(\d+)$");
        if (m.Success) { w = int.Parse(m.Groups[1].Value); h = int.Parse(m.Groups[2].Value); }

        switch (p.Style)
        {
            case "unity":
                if (p.WinMode == "full") a.Add("-screen-fullscreen 1");
                else if (p.WinMode == "window") a.Add("-screen-fullscreen 0");
                else if (p.WinMode == "borderless") a.Add("-popupwindow");
                if (w > 0) a.Add($"-screen-width {w} -screen-height {h}");
                break;
            case "unreal":
                if (p.WinMode == "full") a.Add("-fullscreen");
                else if (p.WinMode is "window" or "borderless") a.Add("-windowed");
                if (w > 0) a.Add($"-ResX={w} -ResY={h}");
                break;
            default: // source / CS2
                if (p.WinMode == "full") a.Add("-fullscreen");
                else if (p.WinMode == "window") a.Add("-windowed");
                else if (p.WinMode == "borderless") a.Add("-windowed -noborder");
                if (w > 0) a.Add($"-w {w} -h {h}");
                if (p.Hz > 0) a.Add($"-freq {p.Hz}");
                break;
        }
        if (!string.IsNullOrWhiteSpace(p.Extra)) a.Add(p.Extra.Trim());
        return string.Join(" ", a);
    }

    // ---------------- Executável do jogo ----------------

    private static readonly string[] IgnoredExe =
        { "unins", "crash", "redist", "vc_", "vcredist", "dxsetup", "dotnet", "easyanticheat", "beservice", "cef", "setup", "uninstall", "launcher_helper", "notification" };

    /// <summary>Procura o .exe principal na pasta do jogo (nome parecido com o do jogo; senão, o maior arquivo). Pode errar: o usuário pode escolher outro.</summary>
    public static string? FindMainExe(string dir, string gameName)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            var exes = new DirectoryInfo(dir).EnumerateFiles("*.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true })
                .Where(f => !IgnoredExe.Any(i => f.Name.Contains(i, StringComparison.OrdinalIgnoreCase))).ToList();
            if (exes.Count == 0) return null;
            string norm = Regex.Replace(gameName.ToLowerInvariant(), "[^a-z0-9]", "");
            var match = exes.Where(f => { string n = Regex.Replace(Path.GetFileNameWithoutExtension(f.Name).ToLowerInvariant(), "[^a-z0-9]", ""); return n.Length >= 3 && (norm.Contains(n) || n.Contains(norm)); })
                            .OrderByDescending(f => f.Length).FirstOrDefault();
            return (match ?? exes.OrderByDescending(f => f.Length).First()).FullName;
        }
        catch { return null; }
    }

    // ---------------- Preferências por jogo no Windows ----------------

    /// <summary>"Configurações gráficas" do Windows: alto desempenho (GPU dedicada) para este .exe. Remove a preferência quando desligado.</summary>
    public static void SetGpuPreference(string exe, bool high, AppSettings settings)
    {
        using var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
        string? previous = k.GetValue(exe) as string;
        if (high)
        {
            if (!settings.GpuPreferenceBackup.ContainsKey(exe))
            {
                settings.GpuPreferenceBackup[exe] = previous;
                try { settings.Save(); }
                catch { settings.GpuPreferenceBackup.Remove(exe); throw; }
            }
            k.SetValue(exe, GpuPreferences.WithHighPerformance(previous), RegistryValueKind.String);
        }
        else if (settings.GpuPreferenceBackup.TryGetValue(exe, out string? original))
        {
            // Restore only our property, retaining options changed by other applications.
            string restored = GpuPreferences.Restore(previous, original);
            if (restored.Length == 0) k.DeleteValue(exe, false);
            else k.SetValue(exe, restored, RegistryValueKind.String);
            settings.GpuPreferenceBackup.Remove(exe);
            try { settings.Save(); }
            catch { settings.GpuPreferenceBackup[exe] = original; throw; }
        }
    }

    /// <summary>Desativa as "otimizações de tela cheia" do Windows para este .exe (aba Compatibilidade).</summary>
    public static void SetDisableFso(string exe, bool disable, AppSettings settings)
    {
        const string flag = "DISABLEDXMAXIMIZEDWINDOWEDMODE";
        using var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers");
        string? current = k.GetValue(exe) as string;
        if (!disable)
        {
            if (!settings.FsoBackup.TryGetValue(exe, out var original)) return;
            if (original == null) k.DeleteValue(exe, false);
            else k.SetValue(exe, original, RegistryValueKind.String);
            settings.FsoBackup.Remove(exe);
            try { settings.Save(); } catch { settings.FsoBackup[exe] = original; throw; }
            return;
        }
        if (!settings.FsoBackup.ContainsKey(exe))
        {
            settings.FsoBackup[exe] = current;
            try { settings.Save(); } catch { settings.FsoBackup.Remove(exe); throw; }
        }
        var tokens = (current ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (!tokens.Contains(flag, StringComparer.OrdinalIgnoreCase)) tokens.Add(flag);
        if (!tokens.Contains("~")) tokens.Insert(0, "~");
        k.SetValue(exe, string.Join(" ", tokens), RegistryValueKind.String);
    }

    // ---------------- Plano de energia ----------------

    private static Task<string> PowerCfg(string args, CancellationToken ct = default) =>
        ProcessRunner.RunAsync("powercfg.exe", args, TimeSpan.FromSeconds(5), ct);

    private static async Task<string?> ActiveScheme(CancellationToken ct)
    {
        var m = Regex.Match(await PowerCfg("/getactivescheme", ct), @"[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}");
        return m.Success ? m.Value : null;
    }

    // ---------------- Iniciar ----------------

    private static Process? FindGameProcess(string dir, string? exe)
    {
        Process? found = null;
        foreach (var pr in Process.GetProcesses())
        {
            bool keep = false;
            try
            {
                string? path = pr.MainModule?.FileName;
                if (found == null && path != null &&
                    ((exe != null && path.Equals(exe, StringComparison.OrdinalIgnoreCase)) || path.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
                { found = pr; keep = true; }
            }
            catch (Exception ex) { DiagnosticLog.Write("GameLauncher", ex); }
            if (!keep) pr.Dispose();
        }
        return found;
    }

    /// <summary>
    /// Aplica o perfil, inicia o jogo e acompanha o processo até ele fechar. Ao terminar (ou cancelar) desfaz o que for temporário.
    /// <paramref name="learnedExe"/> é chamado quando o .exe real do jogo é descoberto.
    /// </summary>
    public static async Task RunAsync(LibGame g, GameProfile p, Action<string> status, Action<string> learnedExe, CancellationToken ct, AppSettings settings)
    {
        string? oldPlan = null;
        var helpers = new List<(Process Proc, bool Close)>();
        try
        {
            status("Aplicando o perfil…");
            if (p.HighPerfPlan)
            {
                oldPlan = await ActiveScheme(ct) ?? throw new InvalidOperationException("Não foi possível ler o plano de energia atual.");
                if (!string.Equals(oldPlan, HighPerformance, StringComparison.OrdinalIgnoreCase))
                {
                    if (!(await PowerCfg("/list", ct)).Contains(HighPerformance, StringComparison.OrdinalIgnoreCase))
                        await PowerCfg($"-duplicatescheme {HighPerformance} {HighPerformance}", ct);
                    await PowerCfg($"/setactive {HighPerformance}", ct);
                }
                else oldPlan = null; // já estava em Alto desempenho: nada a restaurar
            }

            string? exe = p.ExePath ?? g.ExePath ?? await Task.Run(() => FindMainExe(g.InstallDir, g.Name), ct);
            if (exe != null && File.Exists(exe))
            {
                SetGpuPreference(exe, p.GpuHigh, settings);
                try { SetDisableFso(exe, p.DisableFso, settings); } catch (Exception ex) { DiagnosticLog.Write("GameLauncher", ex); }
            }

            foreach (var h in p.Helpers.Where(h => File.Exists(h.Path)))
            {
                status($"Abrindo {Path.GetFileName(h.Path)}…");
                try
                {
                    var proc = Process.Start(new ProcessStartInfo(h.Path, h.Args) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(h.Path) ?? "" });
                    if (proc != null) helpers.Add((proc, h.CloseWithGame));
                }
                catch (Exception ex) { DiagnosticLog.Write("GameLauncher", ex); }
            }
            if (helpers.Count > 0) await Task.Delay(1500, ct);

            status($"Iniciando {g.Name}…");
            string args = (g.Args + " " + BuildArgs(p)).Trim();
            if (g.IsSteam) Process.Start(new ProcessStartInfo($"steam://rungameid/{g.Id}") { UseShellExecute = true });
            else if (g.LaunchUri != null) Process.Start(new ProcessStartInfo(g.LaunchUri) { UseShellExecute = true });
            else if (exe != null)
            {
                if (args.Length == 0) Process.Start("explorer.exe", $"\"{exe}\""); // abre sem privilégio de administrador
                else Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" });
            }
            else throw new InvalidOperationException("Não achei o executável deste jogo. Escolha o .exe na seção Desempenho.");

            // Espera o processo do jogo aparecer (até 3 min) e acompanha até fechar
            Process? game = null;
            for (int i = 0; i < 120 && game == null; i++)
            {
                await Task.Delay(1500, ct);
                game = await Task.Run(() => FindGameProcess(g.InstallDir, p.ExePath ?? g.ExePath), ct);
            }
            if (game == null) { status("Não detectei o processo do jogo (ele pode ter demorado a abrir). Os ajustes continuam aplicados."); return; }

            while (game != null)
            {
                try
                {
                    string? real = game.MainModule?.FileName;
                    if (real != null) { learnedExe(real); }
                    game.PriorityClass = p.Priority switch { "high" => ProcessPriorityClass.High, "above" => ProcessPriorityClass.AboveNormal, _ => ProcessPriorityClass.Normal };
                }
                catch (Exception ex) { DiagnosticLog.Write("GameLauncher", ex); }
                status($"{g.Name} em execução" + (p.Priority != "normal" ? $" · prioridade {(p.Priority == "high" ? "alta" : "acima do normal")}" : "") + ".");
                try { await game.WaitForExitAsync(ct); } catch (OperationCanceledException) { throw; } catch (Exception ex) { DiagnosticLog.Write("GameLauncher", ex); }
                game.Dispose();
                game = null;
                // alguns jogos fecham um launcher e abrem o jogo de verdade: espera mais um pouco
                for (int i = 0; i < 6 && game == null; i++)
                {
                    await Task.Delay(1000, ct);
                    game = await Task.Run(() => FindGameProcess(g.InstallDir, p.ExePath ?? g.ExePath), ct);
                }
            }
            status($"{g.Name} encerrado. Ajustes temporários desfeitos.");
        }
        catch (OperationCanceledException) { status("Acompanhamento cancelado."); }
        catch (Exception ex) { status("Falhou: " + ex.Message); }
        finally
        {
            if (oldPlan != null) { try { await PowerCfg($"/setactive {oldPlan}"); } catch (Exception ex) { DiagnosticLog.Write("Restaurar plano após jogo", ex); } }
            foreach (var (proc, close) in helpers)
            {
                try
                {
                    if (close && !proc.HasExited)
                    {
                        proc.CloseMainWindow();
                        using var wait = new CancellationTokenSource(1500);
                        try { await proc.WaitForExitAsync(wait.Token); }
                        catch (OperationCanceledException) { if (!proc.HasExited) proc.Kill(); }
                    }
                }
                catch (Exception ex) { DiagnosticLog.Write("Encerrar auxiliar do jogo", ex); }
                finally { proc.Dispose(); }
            }
        }
    }
}
