using System;
using System.Collections.Generic;

namespace AetherFrame.Domain.Rendering;

/// <summary>Which group a font is listed under in the font pickers.</summary>
internal enum FontCategory
{
    /// <summary>AetherFrame's own families and Dalamud's default.</summary>
    AetherFrame,
    Fantasy,
    Script,
    Serif,
    Sans,
    Display,
    Mono,
}

/// <summary>
/// One family of the font library: its persisted id (never changed, since Plates and shared
/// layouts name it), its name, where it is listed, its embedded files' prefix, which styles it has
/// as real faces, and its surface model (<see cref="FontTierPolicy"/>), fitted to its files.
/// </summary>
internal sealed record LibraryFontFamily(
    string Id, string DisplayName, FontCategory Category, string FilePrefix, bool HasBold, bool HasItalic, bool HasBoldItalic, int Glyphs, double Scale);

/// <summary>
/// AetherFrame's font library (the owner's request of October 1, 2026): Google Fonts families
/// under the SIL Open Font License or the Apache License, embedded in the plugin beside AetherFrame's
/// own three families. The families themselves are generated (FontLibrary.Families.cs, from
/// tools/fonts); this is the lookup. Pure logic, no Dalamud.
/// </summary>
internal static partial class FontLibrary
{
    /// <summary>The family with exactly this id, or null. Allocation-free: font lookups run per text, per frame.</summary>
    internal static LibraryFontFamily? Find(string? id) => id is not null && Index.ById.TryGetValue(id, out var family) ? family : null;

    /// <summary>The fonts' licence notices, embedded in the plugin (AetherFrame.csproj) and shown from Help.</summary>
    internal static readonly string[] NoticeResources =
    [
        "AetherFrame.Fonts.THIRD-PARTY-FONT-LICENSES.txt",
        "AetherFrame.Fonts.Library.THIRD-PARTY-FONT-LICENSES.txt",
    ];

    /// <summary>The embedded resource name of a family's face (AetherFrame.csproj's Fonts\Library glob).</summary>
    internal static string ResourceName(LibraryFontFamily family, string style) => "AetherFrame.Fonts.Library." + family.FilePrefix + "-" + style + ".ttf";

    /// <summary>
    /// The face a family draws a style with: the style itself when the family has it, else the
    /// nearest it has (Bold Italic falls back to Bold, then Italic), else Regular.
    /// </summary>
    internal static string FaceStyle(LibraryFontFamily family, bool bold, bool italic) => (bold, italic) switch
    {
        (true, true) when family.HasBoldItalic => "BoldItalic",
        (true, _) when family.HasBold => "Bold",
        (_, true) when family.HasItalic => "Italic",
        _ => "Regular",
    };

    /// <summary>
    /// The (bold, italic) a request draws in for <paramref name="familyId"/>: for a library family,
    /// those of the face <see cref="FaceStyle"/> picks, so two requests drawn with one face share one
    /// handle, and its merged AetherFrame Sans is that face's style too; for any other family,
    /// <paramref name="supportsBold"/> and <paramref name="supportsItalic"/> decide, as they always have.
    /// </summary>
    internal static (bool Bold, bool Italic) EffectiveStyle(string familyId, bool bold, bool italic, bool supportsBold, bool supportsItalic)
    {
        if (Find(familyId) is not { } family)
        {
            return (bold && supportsBold, italic && supportsItalic);
        }

        return FaceStyle(family, bold, italic) switch
        {
            "BoldItalic" => (true, true),
            "Bold" => (true, false),
            "Italic" => (false, true),
            _ => (false, false),
        };
    }

    /// <summary>
    /// The lookup, in a class of its own: field initializers across a partial class's files run in
    /// no defined order, and this one reads <see cref="Families"/>, from the generated file.
    /// </summary>
    private static class Index
    {
        internal static readonly Dictionary<string, LibraryFontFamily> ById = Build();

        private static Dictionary<string, LibraryFontFamily> Build()
        {
            var index = new Dictionary<string, LibraryFontFamily>(StringComparer.Ordinal);
            foreach (var family in Families)
            {
                index.Add(family.Id, family);
            }

            return index;
        }
    }
}
