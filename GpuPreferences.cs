namespace NEXCORE;

internal static class GpuPreferences
{
    private static List<string> OtherOptions(string? value) => (value ?? "")
        .Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Where(p => !p.TrimStart().StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase)).ToList();

    public static string WithHighPerformance(string? value) => string.Join(";", OtherOptions(value).Append("GpuPreference=2")) + ";";

    public static string Restore(string? current, string? original)
    {
        var parts = OtherOptions(current);
        string? preference = (original ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(p => p.TrimStart().StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase));
        if (preference != null) parts.Add(preference);
        return parts.Count == 0 ? "" : string.Join(";", parts) + ";";
    }
}
