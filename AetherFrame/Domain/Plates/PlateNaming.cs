using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AetherFrame.Domain.Plates;

/// <summary>Display-name rules for Plates. Names are labels only: duplicates are allowed and a
/// name never identifies a Plate (its Guid does).</summary>
public static class PlateNaming
{
    public const int MaxNameLength = 64;

    public const string DefaultName = "New Plate";

    private const string CopySuffix = " Copy";

    private const string KeptChangesSuffix = " (kept changes)";

    /// <summary>
    /// Trims the name and folds control characters (e.g. a pasted newline) and Unicode format
    /// characters (zero-width spaces and joiners, byte order marks, bidirectional overrides, tag
    /// characters — none of which show as anything) to spaces, so none of those can hide in a name or
    /// render its neighbours backwards. Other invisible characters, such as variation selectors, are
    /// kept (docs/networking/DecisionRegister.md, L13). Returns false with a player-facing <paramref name="error"/>
    /// for an empty or over-long result.
    /// </summary>
    public static bool TryNormalizeName(string? input, out string normalized, out string? error)
    {
        var text = input ?? string.Empty;
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                // A character outside the Basic Multilingual Plane (an emoji, or a tag character).
                var rune = new Rune(c, text[i + 1]);
                builder.Append(Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format ? " " : rune.ToString());
                i++;
                continue;
            }

            builder.Append(char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format ? ' ' : c);
        }

        normalized = builder.ToString().Trim();

        if (normalized.Length == 0)
        {
            error = "A Plate needs a name.";
            return false;
        }

        if (normalized.Length > MaxNameLength)
        {
            error = $"Plate names can be at most {MaxNameLength} characters.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// "Name Copy", or "Name Copy 2", "Name Copy 3", ... when taken (case-insensitively). Copying
    /// a copy doesn't stack suffixes: "Name Copy" duplicates to "Name Copy 2".
    /// </summary>
    public static string MakeCopyName(string sourceName, IEnumerable<string> existingNames)
    {
        var baseName = StripCopySuffix(string.IsNullOrWhiteSpace(sourceName) ? DefaultName : sourceName.Trim());
        var taken = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);

        var candidate = Fit(baseName, CopySuffix);
        for (var n = 2; taken.Contains(candidate); n++)
        {
            candidate = Fit(baseName, $"{CopySuffix} {n}");
        }

        return candidate;
    }

    /// <summary>"Name", or "Name 2", "Name 3", ... when taken (case-insensitively).</summary>
    public static string MakeUniqueName(string baseName, IEnumerable<string> existingNames)
    {
        var name = string.IsNullOrWhiteSpace(baseName) ? DefaultName : baseName.Trim();
        var taken = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);

        var candidate = Fit(name, string.Empty);
        for (var n = 2; taken.Contains(candidate); n++)
        {
            candidate = Fit(name, $" {n}");
        }

        return candidate;
    }

    /// <summary>
    /// The name of a Plate restored from unsaved changes AetherFrame kept: "Name (kept changes)", or
    /// "Name (kept changes) 2", "... 3", ... when taken (case-insensitively), the name shortened to
    /// keep the suffix within the length cap. A name kept in an edited file is folded as
    /// <see cref="TryNormalizeName"/> folds one.
    /// </summary>
    public static string MakeKeptChangesName(string sourceName, IEnumerable<string> existingNames)
    {
        var source = sourceName ?? string.Empty;
        var baseName = TryNormalizeName(source.Length > MaxNameLength ? source[..MaxNameLength] : source, out var normalized, out _) ? normalized : DefaultName;
        var taken = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);

        var candidate = Fit(baseName, KeptChangesSuffix);
        for (var n = 2; taken.Contains(candidate); n++)
        {
            candidate = Fit(baseName, $"{KeptChangesSuffix} {n}");
        }

        return candidate;
    }

    private static string StripCopySuffix(string name)
    {
        var index = name.LastIndexOf(CopySuffix, StringComparison.OrdinalIgnoreCase);
        if (index <= 0)
        {
            return name;
        }

        var rest = name[(index + CopySuffix.Length)..];
        var isCopySuffix = rest.Length == 0 || (rest[0] == ' ' && rest.Length > 1 && rest[1..].All(char.IsAsciiDigit));
        return isCopySuffix ? name[..index] : name;
    }

    /// <summary>Appends the suffix, shortening the base so the result stays within the length cap.</summary>
    private static string Fit(string baseName, string suffix)
    {
        var room = MaxNameLength - suffix.Length;
        var trimmedBase = baseName.Length > room ? baseName[..room].TrimEnd() : baseName;
        return trimmedBase + suffix;
    }
}
