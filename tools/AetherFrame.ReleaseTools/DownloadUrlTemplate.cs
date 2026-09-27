using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AetherFrame.ReleaseTools;

/// <summary>
/// Where a release package is downloaded from, as a template over the release: for GitHub Releases
/// <c>https://github.com/OWNER/REPO/releases/download/v{version}/{package}</c>. The template must end
/// with <c>{package}</c>, so the URL names the file it serves and every version has its own URL: a
/// repository entry can never point at a ZIP of another version by accident.
/// </summary>
public sealed class DownloadUrlTemplate
{
    private static readonly Regex Placeholder = new(@"\{([^{}]*)\}", RegexOptions.CultureInvariant);
    private static readonly string[] KnownPlaceholders = { "internalName", "version", "package" };

    private DownloadUrlTemplate(string text)
    {
        Text = text;
    }

    public string Text { get; }

    public static DownloadUrlTemplate Parse(string? template)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            throw new ReleaseCheckException("downloadUrlTemplate is empty.");
        }

        var placeholders = Placeholder.Matches(template).Select(m => m.Groups[1].Value).ToList();
        var unknown = placeholders.Where(p => !KnownPlaceholders.Contains(p, StringComparer.Ordinal)).ToList();
        if (unknown.Count > 0)
        {
            throw new ReleaseCheckException($"downloadUrlTemplate has unknown placeholder(s) {string.Join(", ", unknown.Select(u => "{" + u + "}"))}; known: {string.Join(", ", KnownPlaceholders.Select(k => "{" + k + "}"))}.");
        }

        if (!template.EndsWith("/{package}", StringComparison.Ordinal))
        {
            throw new ReleaseCheckException("downloadUrlTemplate must end with '/{package}', the package file name.");
        }

        var candidate = new DownloadUrlTemplate(template);
        // Whatever remains after substitution is literal template text; check it once with sample values.
        candidate.Resolve("Sample", new ProductVersion(1, 2, 3), "Sample-1.2.3.zip");
        return candidate;
    }

    public string Resolve(string internalName, ProductVersion version, string packageFileName)
    {
        var url = Text
            .Replace("{internalName}", internalName, StringComparison.Ordinal)
            .Replace("{version}", version.ToString(), StringComparison.Ordinal)
            .Replace("{package}", packageFileName, StringComparison.Ordinal);

        var uri = Urls.ValidateHttps(url, "download URL");
        if (Urls.LastSegment(uri) != packageFileName)
        {
            throw new ReleaseCheckException($"download URL '{url}' does not end with the package file name '{packageFileName}'.");
        }

        return url;
    }

    public override string ToString() => Text;
}
