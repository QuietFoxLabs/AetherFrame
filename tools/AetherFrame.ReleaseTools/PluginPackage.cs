using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;

namespace AetherFrame.ReleaseTools;

/// <summary>One file in the package, as the ZIP directory lists it.</summary>
public sealed record PackageEntry(string Name, long Length, long CompressedLength, DateTimeOffset LastWriteTime);

/// <summary>
/// The release package: the ZIP DalamudPackager built (latest.zip, staged as
/// &lt;InternalName&gt;-&lt;version&gt;.zip). It holds exactly the three files a Dalamud plugin needs,
/// flat, and nothing else. Opening it checks every entry name before anything is read, and reads the
/// three files with size limits, so a hostile or accidental ZIP fails before it can do harm.
/// </summary>
public sealed class PluginPackage
{
    public const long MaxAssemblyBytes = 64L * 1024 * 1024;
    public const long MaxTextBytes = 1024 * 1024;
    public const int MaxEntries = 64;

    private PluginPackage(string path, long size, string sha256, IReadOnlyList<PackageEntry> entries, byte[] assembly, byte[] manifest, byte[] deps)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        Size = size;
        Sha256 = sha256;
        Entries = entries;
        Assembly = assembly;
        ManifestJson = manifest;
        DepsJson = deps;
    }

    public string Path { get; }

    public string FileName { get; }

    public long Size { get; }

    public string Sha256 { get; }

    public IReadOnlyList<PackageEntry> Entries { get; }

    public byte[] Assembly { get; }

    public byte[] ManifestJson { get; }

    public byte[] DepsJson { get; }

    public static string AssemblyEntryName(string internalName) => internalName + ".dll";

    public static string ManifestEntryName(string internalName) => internalName + ".json";

    public static string DepsEntryName(string internalName) => internalName + ".deps.json";

    public static IReadOnlyList<string> ExpectedEntryNames(string internalName) =>
        new[] { DepsEntryName(internalName), AssemblyEntryName(internalName), ManifestEntryName(internalName) };

    /// <summary>Opens and checks the package, recording every finding; returns null when it cannot be used.</summary>
    public static PluginPackage? Open(string path, string internalName, CheckList checks)
    {
        if (!File.Exists(path))
        {
            checks.Fail("package file", $"not found at {path}.");
            return null;
        }

        var size = new FileInfo(path).Length;
        if (size == 0)
        {
            checks.Fail("package file", $"{System.IO.Path.GetFileName(path)} is empty.");
            return null;
        }

        var sha256 = Checksums.Sha256Hex(path);
        checks.Pass("package file", $"{System.IO.Path.GetFileName(path)}, {size} bytes, SHA-256 {sha256}");

        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(path);
        }
        catch (InvalidDataException e)
        {
            checks.Fail("package format", $"not a ZIP archive: {e.Message}");
            return null;
        }

        using (archive)
        {
            try
            {
                if (archive.Entries.Count > MaxEntries)
                {
                    checks.Fail("package entries", $"{archive.Entries.Count} entries; a plugin package has 3.");
                    return null;
                }

                var entries = archive.Entries
                    .Select(e => new PackageEntry(e.FullName, e.Length, e.CompressedLength, e.LastWriteTime))
                    .ToList();

                if (!PackageEntryPolicy.Check(entries, internalName, checks))
                {
                    return null;
                }

                byte[]? assembly = Read(archive, AssemblyEntryName(internalName), MaxAssemblyBytes, checks);
                byte[]? manifest = Read(archive, ManifestEntryName(internalName), MaxTextBytes, checks);
                byte[]? deps = Read(archive, DepsEntryName(internalName), MaxTextBytes, checks);
                if (assembly is null || manifest is null || deps is null)
                {
                    return null;
                }

                return new PluginPackage(path, size, sha256, entries, assembly, manifest, deps);
            }
            catch (InvalidDataException e)
            {
                // A damaged central directory or entry, or an unsupported compression method: .NET reports
                // these only when the directory is enumerated or an entry is read, not when the file opens.
                checks.Fail("package format", $"the ZIP archive is damaged or uses a feature .NET cannot read: {e.Message}");
                return null;
            }
        }
    }

    private static byte[]? Read(ZipArchive archive, string name, long limit, CheckList checks)
    {
        var entry = archive.GetEntry(name)!;
        if (entry.Length > limit)
        {
            checks.Fail($"size of {name}", $"{entry.Length} bytes; the limit is {limit}.");
            return null;
        }

        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            total += read;
            if (total > limit)
            {
                checks.Fail($"size of {name}", $"more than {limit} bytes when decompressed, although the directory says {entry.Length}.");
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        if (total != entry.Length)
        {
            checks.Fail($"size of {name}", $"{total} bytes when decompressed, although the directory says {entry.Length}.");
            return null;
        }

        checks.Pass($"size of {name}", $"{total} bytes");
        return buffer.ToArray();
    }
}

/// <summary>What may and may not appear in a plugin package.</summary>
public static class PackageEntryPolicy
{
    private static readonly Regex DriveLetter = new(@"^[A-Za-z]:", RegexOptions.CultureInvariant);

    /// <summary>Why an entry name is unacceptable in any package, or null when its shape is fine.</summary>
    public static string? Problem(string name)
    {
        if (name.Length == 0)
        {
            return "an entry has an empty name";
        }

        if (name.Length > 255)
        {
            return $"'{Shorten(name)}' is longer than 255 characters";
        }

        if (name.Any(c => c < ' ' || c == (char)0x7F))
        {
            return $"'{Shorten(name)}' contains control characters";
        }

        if (name.Contains('\\', StringComparison.Ordinal))
        {
            return $"'{name}' contains a backslash";
        }

        if (name.StartsWith('/'))
        {
            return $"'{name}' is an absolute path";
        }

        if (DriveLetter.IsMatch(name))
        {
            return $"'{name}' starts with a drive letter";
        }

        if (name.EndsWith('/'))
        {
            return $"'{name}' is a directory entry";
        }

        var segments = name.Split('/');
        if (segments.Any(s => s == ".."))
        {
            return $"'{name}' climbs out of the package (path traversal)";
        }

        if (segments.Any(s => s == "."))
        {
            return $"'{name}' has a '.' path segment";
        }

        if (segments.Any(s => s.Length == 0))
        {
            return $"'{name}' has an empty path segment";
        }

        if (segments.Length > 1)
        {
            return $"'{name}' is inside a folder; the package is flat";
        }

        return null;
    }

    /// <summary>What kind of file an unexpected flat entry is, for the failure message.</summary>
    public static string Categorize(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.EndsWith(".pdb", StringComparison.Ordinal))
        {
            return "debug symbols";
        }

        if (lower.EndsWith(".cs", StringComparison.Ordinal) || lower.EndsWith(".csproj", StringComparison.Ordinal) || lower.EndsWith(".sln", StringComparison.Ordinal)
            || lower.EndsWith(".slnx", StringComparison.Ordinal) || lower.EndsWith(".props", StringComparison.Ordinal) || lower.EndsWith(".targets", StringComparison.Ordinal)
            || lower.EndsWith(".resx", StringComparison.Ordinal))
        {
            return "a source file";
        }

        if (lower.Contains("tests.dll", StringComparison.Ordinal) || lower.StartsWith("xunit", StringComparison.Ordinal) || lower.StartsWith("testhost", StringComparison.Ordinal)
            || lower.StartsWith("microsoft.testplatform", StringComparison.Ordinal) || lower.StartsWith("microsoft.visualstudio.testplatform", StringComparison.Ordinal)
            || lower.StartsWith("nunit", StringComparison.Ordinal))
        {
            return "a test assembly";
        }

        if (lower.EndsWith(".user", StringComparison.Ordinal) || lower == "launchsettings.json" || lower.StartsWith("appsettings", StringComparison.Ordinal)
            || lower == "dalamudconfig.json" || lower.EndsWith(".config", StringComparison.Ordinal) || lower.EndsWith(".runtimeconfig.json", StringComparison.Ordinal))
        {
            return "local configuration";
        }

        if (lower.Contains("xivlauncher", StringComparison.Ordinal) || lower.Contains("pluginconfigs", StringComparison.Ordinal) || lower.Contains("appdata", StringComparison.Ordinal))
        {
            return "a user data path";
        }

        if (lower.StartsWith(".git", StringComparison.Ordinal) || lower.StartsWith(".vs", StringComparison.Ordinal) || lower == ".editorconfig" || lower.EndsWith(".md", StringComparison.Ordinal)
            || lower == ".ds_store" || lower == "thumbs.db" || lower.EndsWith(".log", StringComparison.Ordinal) || lower.EndsWith(".tmp", StringComparison.Ordinal)
            || lower.EndsWith(".bak", StringComparison.Ordinal) || lower.EndsWith(".orig", StringComparison.Ordinal) || lower.EndsWith(".zip", StringComparison.Ordinal))
        {
            return "a development-only file";
        }

        return "an unexpected file";
    }

    /// <summary>Records the entry checks; returns false when the package cannot be used.</summary>
    public static bool Check(IReadOnlyList<PackageEntry> entries, string internalName, CheckList checks)
    {
        var failed = false;

        var problems = entries.Select(e => Problem(e.Name)).Where(p => p is not null).ToList();
        if (problems.Count > 0)
        {
            checks.Fail("entry names", string.Join("; ", problems));
            failed = true;
        }
        else
        {
            checks.Pass("entry names", "flat, relative, no traversal");
        }

        var names = entries.Select(e => e.Name).ToList();
        var duplicates = names.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        var caseCollisions = names.Distinct(StringComparer.Ordinal).GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => string.Join(" and ", g)).ToList();
        if (duplicates.Count > 0 || caseCollisions.Count > 0)
        {
            var parts = new List<string>();
            if (duplicates.Count > 0)
            {
                parts.Add($"more than one entry named {string.Join(", ", duplicates)}");
            }

            if (caseCollisions.Count > 0)
            {
                parts.Add($"entries that differ only by case: {string.Join("; ", caseCollisions)}");
            }

            checks.Fail("unique entries", string.Join("; ", parts));
            failed = true;
        }
        else
        {
            checks.Pass("unique entries");
        }

        if (failed)
        {
            return false;
        }

        var expected = PluginPackage.ExpectedEntryNames(internalName);
        var unexpected = names.Where(n => !expected.Contains(n, StringComparer.Ordinal)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var missing = expected.Where(n => !names.Contains(n, StringComparer.Ordinal)).ToList();
        if (unexpected.Count > 0)
        {
            checks.Fail("no extra files", string.Join("; ", unexpected.Select(n => $"{n} ({Categorize(n)})")));
            failed = true;
        }
        else
        {
            checks.Pass("no extra files");
        }

        if (missing.Count > 0)
        {
            var hints = missing.Select(m =>
            {
                var other = names.FirstOrDefault(n => string.Equals(n, m, StringComparison.OrdinalIgnoreCase));
                return other is null ? m : $"{m} (the package has '{other}', which differs in case)";
            });
            checks.Fail("required files", $"missing {string.Join(", ", hints)}");
            failed = true;
        }
        else
        {
            checks.Pass("required files", string.Join(", ", expected));
        }

        return !failed;
    }

    private static string Shorten(string name) => name.Length > 40 ? name[..40] + "..." : name;
}
