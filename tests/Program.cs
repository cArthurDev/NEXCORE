using System.IO;
using NEXCORE;

string root = Path.Combine(Path.GetTempPath(), "NEXCORE-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
AppSettings.StorageOverride = Path.Combine(root, "settings");
AppSettings.LegacyOverride = Path.Combine(root, "legacy");
int passed = 0;
void Assert(bool condition, string description)
{
    if (!condition) throw new Exception(description);
}
void Test(string name, Action action)
{
    action(); passed++;
    Console.WriteLine("PASS " + name);
}
try
{
    Test("Atomic write preserves the previous version", () =>
    {
        string path = Path.Combine(root, "atomic.json");
        AtomicFile.Write(path, "first"); AtomicFile.Write(path, "second");
        Assert(File.ReadAllText(path) == "second" && File.ReadAllText(path + ".bak") == "first", "Atomic backup missing");
    });
    Test("Settings recover a corrupted primary without overwriting the backup", () =>
    {
        var s = new AppSettings(); s.TweakBackup["power"] = "original"; s.Save(); s.Save();
        File.WriteAllText(Path.Combine(AppSettings.Folder, "settings.json"), "{");
        Assert(AppSettings.Load().TweakBackup["power"] == "original", "Recovery lost system backup");
        Assert(Directory.GetFiles(AppSettings.Folder, "settings.json.corrupt-*").Length == 1, "Corrupt source not retained");
    });
    Test("Partial restore retains failed and unknown backups", () =>
    {
        var s = new AppSettings();
        s.TweakBackup["ok"] = "before"; s.TweakBackup["bad"] = "before"; s.TweakBackup["future"] = "before";
        var failures = WindowsTweaks.RestoreAll(new[] {
            new Tweak { Id = "ok", Restore = _ => { } },
            new Tweak { Id = "bad", Restore = _ => throw new IOException("simulated failure") }
        }, s);
        Assert(failures.Count == 1 && !s.TweakBackup.ContainsKey("ok") && s.TweakBackup.ContainsKey("bad") && s.TweakBackup.ContainsKey("future"), "Partial restoration discarded backup");
        Assert(AppSettings.Load().TweakBackup.ContainsKey("bad"), "Failed backup not persisted");
    });
    Test("Original state is durable before applying a tweak", () =>
    {
        var s = new AppSettings(); s.BackupTweak("setting", "original"); s.BackupTweak("setting", "changed");
        Assert(AppSettings.Load().TweakBackup["setting"] == "original", "Original value overwritten");
    });
    Test("GPU preference preserves unrelated properties", () =>
    {
        string changed = GpuPreferences.WithHighPerformance("AutoHDREnable=1;GpuPreference=1;");
        Assert(changed.Contains("AutoHDREnable=1;") && changed.Contains("GpuPreference=2;") && !changed.Contains("GpuPreference=1;"), "Preference replacement corrupted options");
        string restored = GpuPreferences.Restore(changed + "Other=3;", "GpuPreference=1;");
        Assert(restored.Contains("Other=3;") && restored.Contains("GpuPreference=1;"), "Restore discarded new options");
    });
    Test("Missing sensors produce insufficient data", () =>
    {
        var sample = new LoadSample(null, null, null, null, null, null, null, null, false, null);
        Assert(sample.Classify() == Limiter.Unknown, "Missing data interpreted as healthy");
    });
    Test("Settings migration retains restore data", () =>
    {
        AppSettings.StorageOverride = Path.Combine(root, "migration");
        Directory.CreateDirectory(AppSettings.LegacyOverride!);
        File.WriteAllText(Path.Combine(AppSettings.LegacyOverride!, "settings.json"), "{\"TweakBackup\":{\"power\":\"legacy-plan\"}}");
        var s = AppSettings.Load();
        Assert(s.TweakBackup["power"] == "legacy-plan" && File.Exists(Path.Combine(AppSettings.Folder, "settings.json")), "Migration failed");
        Assert(File.Exists(Path.Combine(AppSettings.LegacyOverride!, "settings.json")), "Migration deleted legacy source");
    });
    Test("Unrecoverable settings fail without erasing evidence", () =>
    {
        AppSettings.StorageOverride = Path.Combine(root, "unrecoverable");
        AppSettings.LegacyOverride = Path.Combine(root, "absent");
        Directory.CreateDirectory(AppSettings.Folder);
        string path = Path.Combine(AppSettings.Folder, "settings.json"); File.WriteAllText(path, "broken");
        bool failed = false; try { AppSettings.Load(); } catch (IOException) { failed = true; }
        Assert(failed && File.ReadAllText(path) == "broken", "Unreadable backups silently erased");
    });
    Test("Process timeout terminates the command", () =>
    {
        bool timedOut = false;
        try { ProcessRunner.RunAsync("powershell.exe", "-NoProfile -NonInteractive -Command Start-Sleep -Seconds 20", TimeSpan.FromMilliseconds(500)).GetAwaiter().GetResult(); }
        catch (TimeoutException) { timedOut = true; }
        Assert(timedOut, "Timeout ignored");
    });
    Test("Only one load operation can own the coordinator", () =>
    {
        var operations = new OperationCoordinator();
        var first = operations.TryStart("OC");
        Assert(first != null && operations.TryStart("CPU") == null, "Concurrent test admitted");
        first!.Dispose();
        using var second = operations.TryStart("GPU");
        Assert(second != null, "Operation lease was not released");
        first.Dispose();
        Assert(operations.IsBusy, "Old lease released a new operation");
    });
    Test("Cancelled commands terminate and preserve cancellation", () =>
    {
        using var stop = new CancellationTokenSource(500);
        bool cancelled = false;
        try { ProcessRunner.RunAsync("powershell.exe", "-NoProfile -NonInteractive -Command Start-Sleep -Seconds 20", TimeSpan.FromSeconds(30), stop.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { cancelled = true; }
        Assert(cancelled, "Cancellation was not propagated");
    });
    Test("Thermal protection stops excessive, missing and invalid readings", () =>
    {
        Assert(!ThermalSafety.MustStop(true, true, 65, 70, 90, 85), "Valid temperatures rejected");
        Assert(ThermalSafety.MustStop(true, false, 90, null, 90, 85), "CPU limit ignored");
        Assert(ThermalSafety.MustStop(false, true, null, 85, 90, 85), "GPU limit ignored");
        Assert(ThermalSafety.MustStop(true, false, null, null, 90, 85), "Missing sensor ignored");
        Assert(ThermalSafety.MustStop(false, true, null, float.NaN, 90, 85), "Invalid sensor accepted");
    });
    Test("Only valid Steam IDs produce remote cover URLs", () =>
    {
        Assert(GameCoverService.RemoteUrls(new LibGame { Store = "Steam", Id = "730" }).Count() == 2, "Valid Steam ID rejected");
        Assert(!GameCoverService.RemoteUrls(new LibGame { Store = "Steam", Id = "../730" }).Any(), "Invalid ID accepted");
        Assert(!GameCoverService.RemoteUrls(new LibGame { Store = "Epic Games", Id = "730" }).Any(), "Non-Steam ID used as Steam ID");
    });
    Test("Cover cache paths cannot escape the cache directory", () =>
    {
        string cache = GameCoverService.CachePath(new LibGame { Store = "Manual", Id = "../../outside.exe" });
        Assert(Path.GetDirectoryName(cache) == Path.Combine(AppSettings.Folder, "covers"), "Cache traversal");
    });
    Test("Custom cover survives source removal and works offline", () =>
    {
        var image = System.Windows.Media.Imaging.BitmapSource.Create(2, 3, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[24], 8);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        string source = Path.Combine(root, "cover.png");
        using (var stream = File.Create(source)) encoder.Save(stream);
        var game = new LibGame { Store = "Manual", Id = "demo", Name = "Demo" };
        var saved = GameCoverService.SaveCustom(game, source); File.Delete(source);
        var loaded = GameCoverService.LoadAsync(game, CancellationToken.None).GetAwaiter().GetResult();
        Assert(saved.IsFrozen && loaded != null && loaded.IsFrozen, "Cover not loaded from private cache");
    });
    Test("Invalid covers do not break the library", () =>
    {
        var game = new LibGame { Store = "Manual", Id = "broken" };
        string path = GameCoverService.CachePath(game); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "not an image");
        Assert(GameCoverService.LoadAsync(game, CancellationToken.None).GetAwaiter().GetResult() == null, "Invalid image was accepted");
    });
    if (args.Contains("--cover-smoke"))
    {
        Test("Steam CDN serves a decodable cover", () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            var game = new LibGame { Store = "Steam", Id = "730", Name = "Counter-Strike 2" };
            var image = Task.Run(() => GameCoverService.LoadAsync(game, timeout.Token)).GetAwaiter().GetResult();
            Assert(image != null && image.IsFrozen, "No Steam cover received");
            Console.WriteLine($"Steam cover: {image!.PixelWidth}x{image.PixelHeight}");
        });
    }
    Test("Temp cleanup removes contents and preserves the selected root", () =>
    {
        string target = Path.Combine(root, "cleanup"); Directory.CreateDirectory(Path.Combine(target, "nested"));
        File.WriteAllBytes(Path.Combine(target, "first.tmp"), new byte[11]);
        File.WriteAllBytes(Path.Combine(target, "nested", "second.tmp"), new byte[19]);
        string neighbor = Path.Combine(root, "keep.txt"); File.WriteAllText(neighbor, "keep");
        var result = TempCleaner.Clean(new[] { target, Path.Combine(target, "nested"), target }, CancellationToken.None, minimumAge: TimeSpan.Zero);
        Assert(result.DeletedFiles == 2 && result.FreedBytes == 30 && result.RemovedFolders == 1, "Unexpected cleanup counts");
        Assert(Directory.Exists(target) && !Directory.EnumerateFileSystemEntries(target).Any() && File.Exists(neighbor), "Cleanup escaped its root");
    });
    Test("Temp cleanup skips locked files and continues", () =>
    {
        string target = Path.Combine(root, "locked-cleanup"); Directory.CreateDirectory(target);
        string locked = Path.Combine(target, "locked.tmp"); File.WriteAllText(locked, "keep");
        File.WriteAllText(Path.Combine(target, "free.tmp"), "remove");
        using var handle = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = TempCleaner.Clean(new[] { target }, CancellationToken.None, minimumAge: TimeSpan.Zero);
        Assert(result.DeletedFiles == 1 && result.Skipped >= 1 && File.Exists(locked), "Locked file handling failed");
    });
    Test("Cancelled cleanup preserves files", () =>
    {
        string target = Path.Combine(root, "cancel-cleanup"); Directory.CreateDirectory(target);
        string file = Path.Combine(target, "keep.tmp"); File.WriteAllText(file, "keep");
        using var stop = new CancellationTokenSource(); stop.Cancel();
        var result = TempCleaner.Clean(new[] { target }, stop.Token);
        Assert(result.Cancelled && result.DeletedFiles == 0 && File.Exists(file), "Cancellation ignored");
    });
    Test("Cleanup refuses protected paths before deleting anything", () =>
    {
        Assert(!TempCleaner.IsSafeRoot(Path.GetPathRoot(root)!) && !TempCleaner.IsSafeRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), "Unsafe root accepted");
        string target = Path.Combine(root, "validate-cleanup"); Directory.CreateDirectory(target);
        string file = Path.Combine(target, "keep.tmp"); File.WriteAllText(file, "keep");
        bool refused = false;
        try { TempCleaner.Clean(new[] { target, Path.GetPathRoot(root)! }, CancellationToken.None); }
        catch (InvalidOperationException) { refused = true; }
        Assert(refused && File.Exists(file), "Cleanup started before validating all paths");
    });
    Test("Cleanup skips junctions without deleting their targets", () =>
    {
        string target = Path.Combine(root, "junction-cleanup"); Directory.CreateDirectory(target);
        string outside = Path.Combine(root, "junction-outside"); Directory.CreateDirectory(outside);
        string file = Path.Combine(outside, "keep.tmp"); File.WriteAllText(file, "keep");
        string link = Path.Combine(target, "external");
        string quote(string value) => "'" + value.Replace("'", "''") + "'";
        ProcessRunner.Run("powershell.exe", "-NoProfile -NonInteractive -Command \"New-Item -ItemType Junction -Path " + quote(link) + " -Target " + quote(outside) + " -ErrorAction Stop | Out-Null\"");
        try
        {
            var result = TempCleaner.Clean(new[] { target }, CancellationToken.None);
            Assert(result.Skipped >= 1 && File.Exists(file) && Directory.Exists(link), "Junction was followed or removed");
            Assert(!TempCleaner.IsSafeRoot(link), "Junction accepted as cleanup root");
        }
        finally { Directory.Delete(link, recursive: false); }
    });
    ReviewChecks.Run(root, Test, Assert);
    Console.WriteLine($"{passed} tests passed.");
    int installerOption = Array.IndexOf(args, "--installer-condition");
    if (installerOption >= 0) InstallerConditionChecks.Run(Path.GetFullPath(args[installerOption + 1]));
}
finally
{
    AppSettings.StorageOverride = null; AppSettings.LegacyOverride = null;
    Directory.Delete(root, true);
}
