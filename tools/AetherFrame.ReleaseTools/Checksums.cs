using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AetherFrame.ReleaseTools;

/// <summary>
/// SHA-256 checksum files in the format <c>sha256sum</c> writes and checks: one
/// <c>&lt;64 lowercase hex&gt;  &lt;file name&gt;</c> line per file, LF line ends, sorted by file
/// name so the same files always give the same bytes. File names are plain names, since release
/// assets sit side by side.
/// </summary>
public static class Checksums
{
    public const string DefaultFileName = "SHA256SUMS.txt";

    private static readonly Regex Line = new(@"^([0-9a-fA-F]{64}) [ *](\S.*)$", RegexOptions.CultureInvariant);

    public static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string Sha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>Formats entries as a checksum file: sorted by name, one line each, trailing LF.</summary>
    public static string Format(IEnumerable<(string Name, string Hash)> entries)
    {
        var list = entries.ToList();
        var duplicates = list.GroupBy(e => e.Name, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
        {
            throw new ReleaseCheckException($"more than one file is named {string.Join(", ", duplicates)}; a checksum file needs unique names.");
        }

        var builder = new StringBuilder();
        foreach (var (name, hash) in list.OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            builder.Append(hash.ToLowerInvariant()).Append("  ").Append(name).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>Hashes the given files and formats them under their file names.</summary>
    public static string ForFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            throw new ReleaseCheckException("no files to checksum.");
        }

        var entries = new List<(string, string)>();
        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                throw new ReleaseCheckException($"file not found: {path}");
            }

            var name = Path.GetFileName(path);
            ValidateName(name);
            entries.Add((name, Sha256Hex(path)));
        }

        return Format(entries);
    }

    public static IReadOnlyList<(string Hash, string Name)> Parse(string text, string what)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length > 0 && lines[^1].Length == 0)
        {
            lines = lines[..^1];
        }

        if (lines.All(l => l.Trim().Length == 0))
        {
            throw new ReleaseCheckException($"{what} is empty.");
        }

        var entries = new List<(string, string)>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Length; i++)
        {
            var match = Line.Match(lines[i]);
            if (!match.Success)
            {
                throw new ReleaseCheckException($"{what} line {i + 1} is not '<sha256>  <file name>': '{lines[i]}'.");
            }

            var name = match.Groups[2].Value;
            ValidateName(name, $"{what} line {i + 1}");
            if (!names.Add(name))
            {
                throw new ReleaseCheckException($"{what} lists {name} more than once.");
            }

            entries.Add((match.Groups[1].Value.ToLowerInvariant(), name));
        }

        return entries;
    }

    /// <summary>Checks every file a checksum file lists, in the given directory, recording one check per file.</summary>
    public static void Verify(string checksumsPath, string directory, CheckList checks)
    {
        if (!File.Exists(checksumsPath))
        {
            checks.Fail("checksum file", $"not found at {checksumsPath}.");
            return;
        }

        var entries = checks.Attempt("checksum file", () => Parse(File.ReadAllText(checksumsPath), Path.GetFileName(checksumsPath)), e => $"{e.Count} file(s) listed");
        if (entries is null)
        {
            return;
        }

        foreach (var (hash, name) in entries)
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path))
            {
                checks.Fail($"checksum of {name}", "the file is missing.");
                continue;
            }

            var actual = Sha256Hex(path);
            checks.Require(actual == hash, $"checksum of {name}", actual, $"is {actual}, listed as {hash}.");
        }
    }

    /// <summary>The checksum a checksum file lists for one file name, or a failure when it is absent.</summary>
    public static string Listed(string checksumsPath, string fileName)
    {
        if (!File.Exists(checksumsPath))
        {
            throw new ReleaseCheckException($"checksum file not found at {checksumsPath}.");
        }

        var entries = Parse(File.ReadAllText(checksumsPath), Path.GetFileName(checksumsPath));
        var entry = entries.FirstOrDefault(e => e.Name == fileName);
        if (entry.Name is null)
        {
            throw new ReleaseCheckException($"{Path.GetFileName(checksumsPath)} has no line for {fileName}.");
        }

        return entry.Hash;
    }

    private static void ValidateName(string name, string what = "checksum entry")
    {
        if (name.Length == 0 || name.IndexOfAny(new[] { '/', '\\' }) >= 0 || name is "." or ".." || name.Any(c => c < ' ' || c == (char)0x7F))
        {
            throw new ReleaseCheckException($"{what}: '{name}' is not a plain file name.");
        }
    }
}
