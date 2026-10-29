using System.IO;
using System.Security.Cryptography.X509Certificates;

namespace NEXCORE;

internal static class TrustedLibrary
{
    public static string Validate(string path, string publisher)
    {
        path = Path.GetFullPath(path);
        if (CorsairSdkInstaller.WinVerifyTrustFile(path) != 0)
            throw new InvalidOperationException("Biblioteca sem assinatura válida: " + Path.GetFileName(path));
#pragma warning disable SYSLIB0057
        using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
        if (!cert.GetNameInfo(X509NameType.SimpleName, false).Contains(publisher, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Fabricante inesperado para " + Path.GetFileName(path));
        return path;
    }
}
