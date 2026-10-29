using System.Runtime.InteropServices;
using System.Text;

internal static class InstallerConditionChecks
{
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiOpenPackageEx(string path, uint options, out uint handle);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiDoAction(uint handle, string action);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiGetProperty(uint handle, string name, StringBuilder buffer, ref uint length);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiSetProperty(uint handle, string name, string value);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern int MsiEvaluateCondition(uint handle, string condition);
    [DllImport("msi.dll")] private static extern uint MsiCloseHandle(uint handle);
    [DllImport("msi.dll")] private static extern uint MsiSetInternalUI(uint level, IntPtr owner);

    internal static void Run(string package)
    {
        MsiSetInternalUI(2, IntPtr.Zero);
        uint error = MsiOpenPackageEx(package, 1, out uint session);
        if (error != 0) throw new Exception("Unable to open MSI inspection session: " + error);
        const string condition = "Installed OR (VersionNT64 AND NEXCORE_WINDOWS_BUILD >= 10240)";
        try
        {
            // Run only the read-only registry search; never run installation actions.
            error = MsiDoAction(session, "AppSearch");
            if (error != 0) throw new Exception("AppSearch failed: " + error);
            string Get(string name)
            {
                var buffer = new StringBuilder(512); uint size = 512;
                if (MsiGetProperty(session, name, buffer, ref size) != 0) throw new Exception("MSI property unavailable: " + name);
                return buffer.ToString();
            }
            void Set(string name, string value)
            {
                if (MsiSetProperty(session, name, value) != 0) throw new Exception("MSI property could not be set: " + name);
            }
            Console.WriteLine($"Windows Installer: legacy build {Get("WindowsBuild")}; registry build {Get("NEXCORE_WINDOWS_BUILD")}");
            if (Environment.Is64BitOperatingSystem && int.TryParse(Get("NEXCORE_WINDOWS_BUILD"), out int build) && build >= 10240)
                if (MsiEvaluateCondition(session, condition) != 1) throw new Exception("Current supported Windows incorrectly rejected");
            Set("Installed", ""); Set("VersionNT64", "603");
            foreach (string buildNumber in new[] { "10240", "19045", "22000", "22631", "26200" })
            {
                Set("NEXCORE_WINDOWS_BUILD", buildNumber);
                if (MsiEvaluateCondition(session, condition) != 1) throw new Exception("Supported Windows rejected: " + buildNumber);
            }
            foreach (string buildNumber in new[] { "9600", "7601", "", "invalid" })
            {
                Set("NEXCORE_WINDOWS_BUILD", buildNumber);
                if (MsiEvaluateCondition(session, condition) != 0) throw new Exception("Unsupported Windows accepted: " + buildNumber);
            }
            Set("NEXCORE_WINDOWS_BUILD", "26200"); Set("VersionNT64", "");
            if (MsiEvaluateCondition(session, condition) != 0) throw new Exception("32-bit Windows accepted");
            Set("Installed", "1");
            if (MsiEvaluateCondition(session, condition) != 1) throw new Exception("Maintenance blocked");
            Console.WriteLine("PASS Installer OS condition: real registry lookup, Windows 10/11, unsupported versions, missing data, x86 and maintenance.");
        }
        finally { MsiCloseHandle(session); }
    }
}
