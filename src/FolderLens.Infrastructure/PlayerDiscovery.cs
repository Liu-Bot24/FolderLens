using Microsoft.Win32;

namespace FolderLens.Infrastructure;

public sealed record PlayerCandidates(IReadOnlyList<string> Paths, bool Incomplete);

public static class PlayerDiscovery
{
    private static readonly string[] Executables = ["PotPlayerMini64.exe", "PotPlayerMini.exe"];

    public static string? NormalizeRegisteredPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
        if (value.Contains('"') || value.Any(char.IsControl) || !Path.IsPathFullyQualified(value)) return null;
        try
        {
            if (!Executables.Contains(Path.GetFileName(value), StringComparer.OrdinalIgnoreCase)) return null;
            return Path.GetFullPath(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    // Fixed registration keys and common installation locations only; never enumerate drives.
    public static PlayerCandidates ReadCandidates(CancellationToken cancellation = default)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); bool incomplete = false;
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        foreach (string executable in Executables)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                using var registry = RegistryKey.OpenBaseKey(hive, view);
                using var key = registry.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + executable);
                if (NormalizeRegisteredPath(key?.GetValue(null) as string) is { } path) paths.Add(path);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { incomplete = true; }
        }
        foreach (string basePath in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs") })
        foreach (string subPath in new[] { Path.Combine("DAUM", "PotPlayer"), "PotPlayer" })
        foreach (string executable in Executables)
            if (!string.IsNullOrEmpty(basePath)) paths.Add(Path.Combine(basePath, subPath, executable));
        return new(paths.ToArray(), incomplete);
    }
}
