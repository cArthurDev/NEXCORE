using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using NEXCORE;

internal static class ReviewChecks
{
    internal static void Run(string root, Action<string, Action> test, Action<bool, string> assert)
    {
        string? oldFolder = AppSettings.StorageOverride;
        AppSettings.StorageOverride = Path.Combine(root, "review-settings");
        try
        {
            test("Memory optimization lease blocks benchmarks and recovers after failures", () =>
            {
                var coordinator = new OperationCoordinator();
                try
                {
                    using var lease = coordinator.TryStart("RAM");
                    assert(coordinator.CurrentName == "RAM" && coordinator.TryStart("GPU") == null, "Conflicting operation admitted");
                    throw new IOException("simulated optimization failure");
                }
                catch (IOException) { }
                using var next = coordinator.TryStart("CPU");
                assert(next != null && coordinator.CurrentName == "CPU", "Failure retained operation lease");
            });
            test("GPU selection respects integrated GPU and rejects missing selected adapters", () =>
            {
                var adapters = new[] { new Adapter("Integrated", 1), new Adapter("Dedicated", 8) };
                assert(GpuAdapterSelection.Choose(adapters, "integrated", a => a.Name, a => a.Memory) == adapters[0], "Selected GPU replaced by highest VRAM");
                bool rejected = false;
                try { GpuAdapterSelection.Choose(adapters, "Unavailable", a => a.Name, a => a.Memory); }
                catch (InvalidOperationException) { rejected = true; }
                assert(rejected, "Missing GPU silently fell back");
            });
            test("Single instance gate excludes another thread and transfers ownership after release", () =>
            {
                string name = @"Local\NEXCORE.Test." + Guid.NewGuid().ToString("N");
                using (var owner = SingleInstanceGate.TryAcquire(name))
                {
                    assert(owner != null, "First owner denied");
                    bool blocked = Task.Run(() => { using var contender = SingleInstanceGate.TryAcquire(name); return contender == null; }).GetAwaiter().GetResult();
                    assert(blocked, "Second writer admitted");
                }
                using var successor = SingleInstanceGate.TryAcquire(name);
                assert(successor != null, "Successor could not acquire ownership");
            });
            test("Fullscreen backup restores absent, empty and preexisting values exactly", () =>
            {
                const string layers = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
                using var key = Registry.CurrentUser.CreateSubKey(layers);
                foreach (string? original in new string?[] { null, "", "~ HIGHDPIAWARE", "~ DISABLEDXMAXIMIZEDWINDOWEDMODE RUNASADMIN" })
                {
                    string exe = Path.Combine(root, Guid.NewGuid().ToString("N") + ".exe");
                    var settings = new AppSettings();
                    try
                    {
                        if (original != null) key.SetValue(exe, original);
                        GameLauncher.SetDisableFso(exe, false, settings);
                        assert((string?)key.GetValue(exe) == original, "Unmanaged original was modified");
                        GameLauncher.SetDisableFso(exe, true, settings);
                        GameLauncher.SetDisableFso(exe, true, settings);
                        var reloaded = AppSettings.Load();
                        assert(reloaded.FsoBackup[exe] == original, "First backup was overwritten or not persisted");
                        GameLauncher.SetDisableFso(exe, false, reloaded);
                        assert((string?)key.GetValue(exe) == original && !AppSettings.Load().FsoBackup.ContainsKey(exe), "Original state not restored");
                    }
                    finally { key.DeleteValue(exe, false); }
                }
            });
            test("Fullscreen setting remains unchanged when saving its backup fails", () =>
            {
                string exe = Path.Combine(root, "failed-backup.exe");
                using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers");
                string? previousFolder = AppSettings.StorageOverride;
                string blocker = Path.Combine(root, "blocked-settings"); File.WriteAllText(blocker, "file instead of folder");
                AppSettings.StorageOverride = blocker;
                try
                {
                    var settings = new AppSettings(); bool failed = false;
                    try { GameLauncher.SetDisableFso(exe, true, settings); } catch (IOException) { failed = true; }
                    assert(failed && key.GetValue(exe) == null && !settings.FsoBackup.ContainsKey(exe), "Changed setting without durable backup");
                }
                finally { AppSettings.StorageOverride = previousFolder; key.DeleteValue(exe, false); }
            });
            test("Shutdown timeout allows ending a stalled wait", () =>
            {
                var stalled = new TaskCompletionSource(); int prompts = 0; bool aborted = false;
                try { ShutdownWaiter.WaitAsync(stalled.Task, TimeSpan.FromMilliseconds(20), () => { prompts++; return false; }).GetAwaiter().GetResult(); }
                catch (ShutdownAbortedException) { aborted = true; }
                assert(aborted && prompts == 1, "Shutdown wait remained unbounded");
            });
            test("Shutdown can continue waiting and propagates task errors", () =>
            {
                var completion = new TaskCompletionSource();
                ShutdownWaiter.WaitAsync(completion.Task, TimeSpan.FromMilliseconds(20), () => { completion.SetResult(); return true; }).GetAwaiter().GetResult();
                bool propagated = false;
                try { ShutdownWaiter.WaitAsync(Task.FromException(new IOException("failure")), TimeSpan.FromSeconds(1), () => false).GetAwaiter().GetResult(); }
                catch (IOException) { propagated = true; }
                assert(propagated, "Background exception swallowed");
            });
            test("Temp preview is read only and default cleanup preserves recent files", () =>
            {
                string folder = Path.Combine(root, "aged-cleanup"); Directory.CreateDirectory(folder);
                string old = Path.Combine(folder, "old.tmp"), recent = Path.Combine(folder, "recent.tmp");
                File.WriteAllBytes(old, new byte[37]); File.WriteAllBytes(recent, new byte[19]);
                File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-3));
                var preview = TempCleaner.Preview(new[] { folder }, CancellationToken.None, TimeSpan.FromHours(48));
                assert(preview.DeletedFiles == 1 && preview.FreedBytes == 37 && File.Exists(old) && File.Exists(recent), "Preview modified files or included recent data");
                var cleaned = TempCleaner.Clean(new[] { folder }, CancellationToken.None);
                assert(cleaned.DeletedFiles == 1 && !File.Exists(old) && File.Exists(recent), "Default retention ignored");
                var full = TempCleaner.Clean(new[] { folder }, CancellationToken.None, minimumAge: TimeSpan.Zero);
                assert(full.DeletedFiles == 1 && !File.Exists(recent), "Explicit full cleanup ignored");
            });
            test("Cleanup rechecks file age after preview", () =>
            {
                string folder = Path.Combine(root, "changed-after-preview"); Directory.CreateDirectory(folder);
                string file = Path.Combine(folder, "active.tmp"); File.WriteAllText(file, "keep");
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-3));
                assert(TempCleaner.Preview(new[] { folder }, CancellationToken.None, TimeSpan.FromHours(48)).DeletedFiles == 1, "Old file not previewed");
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
                assert(TempCleaner.Clean(new[] { folder }, CancellationToken.None).DeletedFiles == 0 && File.Exists(file), "Newly modified file was deleted");
            });
            test("Automatic cover cache expires old files and preserves custom covers", () =>
            {
                string folder = Path.Combine(AppSettings.Folder, "covers"); Directory.CreateDirectory(folder);
                string automatic = Path.Combine(folder, "steam-old.png"), custom = Path.Combine(folder, "custom-old.png");
                File.WriteAllBytes(automatic, new byte[1]); File.WriteAllBytes(custom, new byte[1]);
                File.SetLastWriteTimeUtc(automatic, DateTime.UtcNow.AddDays(-91)); File.SetLastWriteTimeUtc(custom, DateTime.UtcNow.AddDays(-91));
                GameCoverService.PruneCache();
                assert(!File.Exists(automatic) && File.Exists(custom), "Cache policy deleted custom artwork or retained expired files");
            });
            test("Benchmark context survives settings and report roundtrips and separates presets", () =>
            {
                var context = new BenchmarkContext("CPU", "GPU", "driver", "1.0", "Windows", 8, "1920x1080", 30, new() { ["Preset"] = "High", ["RequestedSeconds"] = "30" }) { MemoryBytes = 16L << 30 };
                var settings = new AppSettings();
                settings.BenchRecords.Add(new BenchRecord(DateTime.UtcNow, "gpu", 123, "summary") { Context = context });
                settings.BenchRuns.Add(new BenchRun(DateTime.UtcNow, 10, 5, false, "CPU"));
                settings.Save(); var loaded = AppSettings.Load();
                assert(loaded.BenchRecords[0].Context?.ComparisonKey == context.ComparisonKey && loaded.BenchRuns[0].Context == null, "Context persistence or backward compatibility failed");
                assert(context.ComparisonKey == (context with { DurationSeconds = 30.1 }).ComparisonKey, "Measured variance changed comparison group");
                assert(context.ComparisonKey != (context with { Driver = "new-driver" }).ComparisonKey && context.ComparisonKey != (context with { Resolution = "2560x1440" }).ComparisonKey, "Incompatible tests grouped together");
                string json = ReportBuilder.Json(new ReportData { Benchmarks = new() { new(DateTime.UtcNow, "GPU", "result") { Context = context } } });
                assert(json.Contains("1920x1080") && json.Contains("MemoryBytes"), "Report omitted benchmark metadata");
            });
            test("Game grid virtualizes large libraries, scrolls and resets after filtering", () => RunSta(() =>
            {
                var list = new ListBox { Width = 650, Height = 700, ItemsSource = Enumerable.Range(0, 1000).ToArray() };
                list.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingGamePanel)));
                var scroll = new FrameworkElementFactory(typeof(ScrollViewer));
                scroll.SetValue(ScrollViewer.CanContentScrollProperty, true);
                scroll.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
                scroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
                scroll.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
                list.Template = new ControlTemplate(typeof(ListBox)) { VisualTree = scroll };
                ScrollViewer.SetCanContentScroll(list, true);
                ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
                ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
                list.Measure(new Size(650, 700)); list.Arrange(new Rect(0, 0, 650, 700)); list.UpdateLayout();
                VirtualizingGamePanel? Find(DependencyObject node)
                {
                    if (node is VirtualizingGamePanel panel) return panel;
                    for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                        if (Find(VisualTreeHelper.GetChild(node, i)) is { } found) return found;
                    return null;
                }
                var panel = Find(list) ?? throw new Exception("Game panel not created");
                assert(VisualTreeHelper.GetChildrenCount(panel) < 30 && list.ItemContainerGenerator.ContainerFromIndex(900) == null, "All game cards were realized");
                panel.SetVerticalOffset(panel.ExtentHeight); list.UpdateLayout();
                assert(list.ItemContainerGenerator.ContainerFromIndex(999) != null && list.ItemContainerGenerator.ContainerFromIndex(0) == null, "Scrolling did not recycle offscreen cards");
                list.ScrollIntoView(list.Items[0]); list.UpdateLayout();
                assert(list.ItemContainerGenerator.ContainerFromIndex(0) != null, "Keyboard/programmatic navigation could not bring first game into view");
                list.Width = 1050; list.Measure(new Size(1050, 700)); list.Arrange(new Rect(0, 0, 1050, 700)); list.UpdateLayout();
                assert(VisualTreeHelper.GetChildrenCount(panel) < 40, "Resizing realized the entire library");
                list.ItemsSource = Enumerable.Range(0, 2).ToArray(); list.UpdateLayout();
                assert(panel.VerticalOffset == 0 && list.ItemContainerGenerator.ContainerFromIndex(0) != null, "Filtering retained an invalid scroll offset");
            }));
        }
        finally { AppSettings.StorageOverride = oldFolder; }
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(20))) throw new TimeoutException("WPF layout test stalled");
        if (failure != null) throw new Exception("WPF layout failed", failure);
    }
    private sealed record Adapter(string Name, ulong Memory);
}
