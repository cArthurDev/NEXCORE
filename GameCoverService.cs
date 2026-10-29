using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace NEXCORE;

internal static class GameCoverService
{
    private const int MaxBytes = 5 * 1024 * 1024;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    internal static string CachePath(LibGame game, bool custom = false) => Path.Combine(AppSettings.Folder, "covers",
        (custom ? "custom-" : "steam-") + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(game.Key))) + ".png");

    internal static void PruneCache()
    {
        try
        {
            string folder = Path.Combine(AppSettings.Folder, "covers");
            if (!Directory.Exists(folder)) return;
            var files = new DirectoryInfo(folder).GetFiles("steam-*.png").OrderBy(f => f.LastWriteTimeUtc).ToList();
            long total = files.Sum(f => f.Length);
            foreach (var file in files)
            {
                if (total <= 128L * 1024 * 1024 && file.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-90)) continue;
                long size = file.Length; file.Delete(); total -= size;
            }
        }
        catch (Exception ex) { DiagnosticLog.Write("Limitar cache de capas", ex); }
    }

    private static BitmapSource Decode(byte[] data)
    {
        if (data.Length > MaxBytes) throw new IOException("Imagem maior que 5 MB.");
        using var stream = new MemoryStream(data);
        var image = new BitmapImage();
        image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 360; image.StreamSource = stream; image.EndInit(); image.Freeze();
        if (image.PixelWidth <= 0 || image.PixelHeight <= 0 || image.PixelHeight > 4096)
            throw new IOException("Dimensões de capa inválidas.");
        return image;
    }

    private static BitmapSource? Read(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxBytes) return null;
            return Decode(File.ReadAllBytes(path));
        }
        catch (Exception ex) { DiagnosticLog.Write("Ler capa", ex); return null; }
    }

    internal static BitmapSource SaveCustom(LibGame game, string source)
    {
        if (new FileInfo(source).Length > MaxBytes) throw new IOException("Escolha uma imagem de até 5 MB.");
        var image = Decode(File.ReadAllBytes(source));
        Save(image, CachePath(game, custom: true));
        return image;
    }

    private static void Save(BitmapSource image, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(temp)) encoder.Save(stream);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal static IEnumerable<string> RemoteUrls(LibGame game)
    {
        // IDs from other stores and arbitrary strings must never form a Steam URL.
        if (!game.IsSteam || game.Id.Length > 12 || game.Id.Length == 0 || !game.Id.All(char.IsAsciiDigit)) yield break;
        yield return $"https://cdn.akamai.steamstatic.com/steam/apps/{game.Id}/library_600x900.jpg";
        yield return $"https://cdn.akamai.steamstatic.com/steam/apps/{game.Id}/header.jpg";
    }

    internal static async Task<BitmapSource?> LoadAsync(LibGame game, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var saved = Read(CachePath(game, true)) ?? Read(CachePath(game));
        if (saved != null && File.Exists(CachePath(game)))
            try { File.SetLastWriteTimeUtc(CachePath(game), DateTime.UtcNow); } catch (IOException) { }
        if (saved != null) return saved;
        if (!game.IsSteam) return null;
        string? steam = SteamInfo.SteamPath();
        if (steam != null && game.Id.All(char.IsAsciiDigit) && game.Id.Length is > 0 and <= 12)
        {
            string cache = Path.Combine(steam, "appcache", "librarycache");
            foreach (string name in new[] { game.Id + "_library_600x900.jpg", game.Id + "_header.jpg" })
                if (Read(Path.Combine(cache, name)) is { } local) return local;
            string appCache = Path.Combine(cache, game.Id);
            if (Directory.Exists(appCache))
                foreach (string file in Directory.EnumerateFiles(appCache, "*library_600x900*.jpg").Take(3))
                    if (Read(file) is { } local) return local;
        }
        foreach (string url in RemoteUrls(game))
        {
            try
            {
                using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound) continue;
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaxBytes) continue;
                await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var data = new MemoryStream(); var buffer = new byte[81920]; int count;
                while ((count = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    if (data.Length + count > MaxBytes) throw new IOException("Capa maior que 5 MB.");
                    data.Write(buffer, 0, count);
                }
                var image = Decode(data.ToArray());
                try { Save(image, CachePath(game)); PruneCache(); }
                catch (Exception ex) { DiagnosticLog.Write("Guardar capa", ex); }
                return image;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or NotSupportedException or SystemException)
            { DiagnosticLog.Write("Buscar capa", ex); }
        }
        return null;
    }
}
