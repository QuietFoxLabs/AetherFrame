using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AetherFrame.ReleaseTools;

/// <summary>
/// The product version as Version.props sets it: MAJOR.MINOR.PATCH with no leading zeros (policy:
/// docs/Versioning.md). Everything else is derived: the assembly and manifest version is
/// MAJOR.MINOR.PATCH.0 and the release tag is vMAJOR.MINOR.PATCH.
/// </summary>
public readonly record struct ProductVersion(int Major, int Minor, int Patch) : IComparable<ProductVersion>
{
    private static readonly Regex Pattern = new(@"^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})$", RegexOptions.CultureInvariant);

    public Version AssemblyVersion => new(Major, Minor, Patch, 0);

    public string Tag => "v" + ToString();

    public static ProductVersion Parse(string text, string what)
    {
        if (!TryParse(text, out var version))
        {
            throw new ReleaseCheckException($"{what} is '{text}', not a MAJOR.MINOR.PATCH version.");
        }

        return version;
    }

    public static bool TryParse(string? text, out ProductVersion version)
    {
        version = default;
        if (text is null)
        {
            return false;
        }

        var match = Pattern.Match(text);
        if (!match.Success)
        {
            return false;
        }

        version = new ProductVersion(
            int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture));
        return true;
    }

    /// <summary>The product version behind a four-part MAJOR.MINOR.PATCH.0 assembly version.</summary>
    public static ProductVersion FromAssemblyVersion(Version version, string what)
    {
        if (version.Build < 0 || version.Revision != 0)
        {
            throw new ReleaseCheckException($"{what} is {version}, not MAJOR.MINOR.PATCH.0.");
        }

        return new ProductVersion(version.Major, version.Minor, version.Build);
    }

    /// <summary>The product version behind a four-part MAJOR.MINOR.PATCH.0 assembly version string, written canonically.</summary>
    public static ProductVersion FromAssemblyVersion(string? text, string what)
    {
        // Version.TryParse also takes spaces, signs and leading zeros ("+0.01.5.0 "); only its canonical text passes.
        if (text is null || text.Split('.').Length != 4 || !Version.TryParse(text, out var version) || version.ToString() != text)
        {
            throw new ReleaseCheckException($"{what} is '{text}', not MAJOR.MINOR.PATCH.0.");
        }

        return FromAssemblyVersion(version, what);
    }

    /// <summary>Reads the one place the version is set, Version.props.</summary>
    public static ProductVersion ReadVersionProps(string path)
    {
        if (!File.Exists(path))
        {
            throw new ReleaseCheckException($"Version.props not found at {path}.");
        }

        XDocument document;
        try
        {
            document = XDocument.Load(path);
        }
        catch (System.Xml.XmlException e)
        {
            throw new ReleaseCheckException($"{path} is not well-formed XML: {e.Message}");
        }

        var versions = document.Root?.Elements("PropertyGroup").Elements("Version").ToList() ?? new();
        if (versions.Count != 1)
        {
            throw new ReleaseCheckException($"{path} must set <Version> exactly once in a <PropertyGroup>; found {versions.Count}.");
        }

        return Parse(versions[0].Value.Trim(), $"{path} <Version>");
    }

    public int CompareTo(ProductVersion other) =>
        Major != other.Major ? Major.CompareTo(other.Major)
        : Minor != other.Minor ? Minor.CompareTo(other.Minor)
        : Patch.CompareTo(other.Patch);

    public static bool operator <(ProductVersion left, ProductVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(ProductVersion left, ProductVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(ProductVersion left, ProductVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(ProductVersion left, ProductVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
}
