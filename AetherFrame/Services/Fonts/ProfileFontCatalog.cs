using System;
using System.Collections.Generic;
using AetherFrame.Domain.Profiles;
using AetherFrame.Domain.Rendering;

namespace AetherFrame.Services.Fonts;

/// <summary>
/// One selectable entry in the Inspector's Font Family control: a display name plus whether the
/// family has a REAL Bold and/or Italic face (not a synthesized one). The Inspector uses these
/// flags to decide which style checkboxes to even show — see
/// <c>ProfileEditorWindow.DrawTypographySection</c> — rather than exposing a control that
/// would silently do nothing, or worse, fake the style geometrically.
/// </summary>
internal sealed record ProfileFontFamilyDescriptor(string Id, string DisplayName, bool SupportsBold, bool SupportsItalic)
{
    /// <summary>The group the font pickers list it under.</summary>
    internal FontCategory Category { get; init; } = FontCategory.AetherFrame;
}

/// <summary>
/// The small, curated, portable set of font families AetherFrame ships with. Every curated
/// family (everything except <see cref="DalamudDefault"/>) is embedded directly in the plugin
/// assembly as real Regular/Bold/Italic/Bold Italic TrueType faces — never depends on an
/// arbitrary locally-installed Windows font, and never fakes Bold/Italic geometrically for a
/// family that lacks the real face. See <see cref="ProfileFontService"/> for how an id here maps
/// to the actual embedded font bytes (or, for <see cref="DalamudDefault"/>, to Dalamud's own
/// built-in default font).
///
/// Deliberately just a static list rather than anything pluggable: adding a family later is a
/// matter of embedding its four TTFs (see the AetherFrame.csproj Fonts glob) and adding one
/// descriptor here (plus its <see cref="Resolve"/> arm), one switch arm in
/// <see cref="ProfileFontService"/>, and its calibrated surface model in
/// <see cref="FontTierPolicy"/> — no architecture changes needed. Real font importing
/// (arbitrary user-supplied files) is intentionally out of scope for now.
/// </summary>
internal static class ProfileFontCatalog
{
    /// <summary>Preserved for legacy compatibility only — every element saved before FontFamily
    /// existed resolves here. No real Bold/Italic face; never offered as the default for new text.</summary>
    internal static readonly ProfileFontFamilyDescriptor DalamudDefault =
        new(ProfileFontFamilies.DalamudDefault, "Dalamud Default", SupportsBold: false, SupportsItalic: false);

    /// <summary>AetherFrame's own default for new text: a complete Regular/Bold/Italic/Bold
    /// Italic family (PT Sans, SIL OFL 1.1).</summary>
    internal static readonly ProfileFontFamilyDescriptor AetherFrameSans =
        new(ProfileFontFamilies.AetherFrameSans, "AetherFrame Sans", SupportsBold: true, SupportsItalic: true);

    /// <summary>A complete serif family (PT Serif, SIL OFL 1.1).</summary>
    internal static readonly ProfileFontFamilyDescriptor AetherFrameSerif =
        new(ProfileFontFamilies.AetherFrameSerif, "AetherFrame Serif", SupportsBold: true, SupportsItalic: true);

    /// <summary>A complete monospace family (Cousine, SIL OFL 1.1).</summary>
    internal static readonly ProfileFontFamilyDescriptor AetherFrameMono =
        new(ProfileFontFamilies.AetherFrameMono, "AetherFrame Mono", SupportsBold: true, SupportsItalic: true);

    /// <summary>Display order for the Inspector's Family combo: AetherFrame's fully-styled
    /// families first (the ones a user should actually pick), legacy compatibility last.</summary>
    internal static readonly IReadOnlyList<ProfileFontFamilyDescriptor> All = BuildAll();

    // The library's descriptors by id, for Resolve (built once; a lookup allocates nothing).
    private static readonly Dictionary<string, ProfileFontFamilyDescriptor> LibraryById = BuildLibraryIndex();

    /// <summary>Resolves a persisted family id to its descriptor, falling back to
    /// <see cref="DalamudDefault"/> for null/unrecognized ids (e.g. a legacy element, or one
    /// saved by a newer build with a family this one doesn't know). A plain switch rather than
    /// a search over <see cref="All"/>: the font cache calls this for every text element it
    /// measures or draws, every frame, and it must not allocate. The same rule as
    /// <see cref="FontTierPolicy.ResolveFamilyId"/>.</summary>
    internal static ProfileFontFamilyDescriptor Resolve(string? familyId) => familyId switch
    {
        ProfileFontFamilies.AetherFrameSans => AetherFrameSans,
        ProfileFontFamilies.AetherFrameSerif => AetherFrameSerif,
        ProfileFontFamilies.AetherFrameMono => AetherFrameMono,
        not null when LibraryById.TryGetValue(familyId, out var library) => library,
        _ => DalamudDefault,
    };

    /// <summary>A library family's descriptor: Bold and Italic are offered when it has either real face.</summary>
    private static ProfileFontFamilyDescriptor Describe(LibraryFontFamily family) =>
        new(family.Id, family.DisplayName, family.HasBold, family.HasItalic) { Category = family.Category };

    /// <summary>AetherFrame's own families first, then the library by category and name, Dalamud Default last.</summary>
    private static IReadOnlyList<ProfileFontFamilyDescriptor> BuildAll()
    {
        var all = new List<ProfileFontFamilyDescriptor> { AetherFrameSans, AetherFrameSerif, AetherFrameMono };
        foreach (var family in FontLibrary.Families)
        {
            all.Add(Describe(family));
        }

        all.Add(DalamudDefault);
        return all.AsReadOnly();
    }

    private static Dictionary<string, ProfileFontFamilyDescriptor> BuildLibraryIndex()
    {
        var index = new Dictionary<string, ProfileFontFamilyDescriptor>(StringComparer.Ordinal);
        foreach (var descriptor in All)
        {
            if (descriptor.Category != FontCategory.AetherFrame)
            {
                index.Add(descriptor.Id, descriptor);
            }
        }

        return index;
    }
}
