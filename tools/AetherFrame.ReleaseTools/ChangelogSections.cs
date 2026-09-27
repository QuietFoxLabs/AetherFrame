using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AetherFrame.ReleaseTools;

/// <summary>
/// A released version's notes in CHANGELOG.md: the lines under <c>## [VERSION] - YYYY-MM-DD</c> up to
/// the next version heading or the link list at the end, exactly as the Release workflow publishes
/// them. That text becomes the entry's Changelog in the repository metadata.
/// </summary>
public static class ChangelogSections
{
    /// <summary>Dalamud shows the changelog in the installer; anything longer than this is a mistake.</summary>
    public const int MaxLength = 16 * 1024;

    private static readonly Regex AnyVersionHeading = new(@"^## \[", RegexOptions.CultureInvariant);
    private static readonly Regex LinkDefinition = new(@"^\[[^\]]+\]:\s", RegexOptions.CultureInvariant);

    public static string Read(string changelogPath, ProductVersion version)
    {
        if (!File.Exists(changelogPath))
        {
            throw new ReleaseCheckException($"changelog not found at {changelogPath}.");
        }

        return Section(File.ReadAllText(changelogPath), version, changelogPath);
    }

    public static string Section(string changelog, ProductVersion version, string what = "CHANGELOG.md")
    {
        var lines = changelog.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var heading = new Regex(@"^## \[" + Regex.Escape(version.ToString()) + @"\] - \d{4}-\d{2}-\d{2}\s*$", RegexOptions.CultureInvariant);

        var starts = Enumerable.Range(0, lines.Length).Where(i => heading.IsMatch(lines[i])).ToList();
        if (starts.Count == 0)
        {
            throw new ReleaseCheckException($"{what} has no '## [{version}] - YYYY-MM-DD' section.");
        }

        if (starts.Count > 1)
        {
            throw new ReleaseCheckException($"{what} has {starts.Count} sections for {version}.");
        }

        var start = starts[0];
        var end = lines.Length;
        for (var i = start + 1; i < lines.Length; i++)
        {
            if (AnyVersionHeading.IsMatch(lines[i]) || LinkDefinition.IsMatch(lines[i]))
            {
                end = i;
                break;
            }
        }

        var section = string.Join("\n", lines.Skip(start + 1).Take(end - start - 1)).Trim();
        if (section.Length == 0)
        {
            throw new ReleaseCheckException($"the {version} section of {what} is empty.");
        }

        if (section.Length > MaxLength)
        {
            throw new ReleaseCheckException($"the {version} section of {what} is {section.Length} characters; the repository changelog is limited to {MaxLength}.");
        }

        return section;
    }
}
