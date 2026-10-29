using System.IO;
using System.Text;

namespace NEXCORE;

internal static class AtomicFile
{
    private static readonly object Sync = new();
    public static void Write(string path, string content)
    {
        lock (Sync)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(content);
                    stream.Write(bytes);
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temp, path, path + ".bak", true);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
