using System;
using System.IO;

namespace AetherFrame.Services.Network.Personas;

/// <summary>Paths as the Sharing window shows them, so a screenshot never shows the Windows user name (N2-9c).</summary>
public static class PersonaPaths
{
    /// <summary>
    /// A path as the window shows it, so that a screenshot or a stream doesn't show the Windows user
    /// name: under the application data folder, from <c>%APPDATA%</c> on; elsewhere under the user's
    /// profile (a launcher installed in Documents, say), from <c>%USERPROFILE%</c> on; anywhere else,
    /// as it is. Both forms work in Explorer's address bar.
    /// </summary>
    public static string DisplayPath(string path, string applicationData, string userProfile = "")
    {
        ArgumentNullException.ThrowIfNull(path);
        return Under(path, applicationData, "%APPDATA%") ?? Under(path, userProfile, "%USERPROFILE%") ?? path;
    }

    private static string? Under(string path, string? folder, string name)
    {
        var root = (folder ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (root.Length > 0
            && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && (path.Length == root.Length || path[root.Length] == Path.DirectorySeparatorChar || path[root.Length] == Path.AltDirectorySeparatorChar))
        {
            return name + path[root.Length..];
        }

        return null;
    }
}
