using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace AetherFrame.Domain.Rendering;

/// <summary>
/// One symbol face merged into a font: the embedded face, what it draws there (ImGui glyph
/// ranges), and its metrics, to size it against the font it is merged into.
/// </summary>
internal sealed record SymbolMerge(string Resource, ushort[] GlyphRanges, FontVerticalMetrics Metrics);

/// <summary>
/// Symbols a Plate's font has no glyph for (issue #121): most of AetherFrame's fonts have no ♥,
/// and ImGui draws a character its font lacks as "?". Two bundled symbol faces, Noto Sans Symbols 2
/// and Noto Sans Symbols (AetherFrame/Fonts/Symbols, cut down to <see cref="Blocks"/> by
/// tools/fonts/build_symbol_fonts.py), draw them instead, merged into the font the text is in:
///
/// <list type="bullet">
/// <item><b>Only what is used.</b> A font is built with the symbols the session's Plates have
/// actually used (<see cref="Take(int)"/>), not with the thousand-odd the faces have, so a Plate
/// with no symbols builds exactly as before and costs nothing more. When a new symbol turns up,
/// the fonts are built again with it (ProfileFontService).</item>
/// <item><b>Bounded.</b> At most <see cref="MaxSymbols"/> in a session: the heaviest that many,
/// at any family's largest tier, still fit that tier into one atlas texture
/// (SymbolFallbackTests). One past that stays "?" until the game restarts.</item>
/// <item><b>The font's own glyphs win.</b> ImGui merges only what the font lacks, so a font that
/// has a symbol (Cousine's ♥) draws its own.</item>
/// <item><b>Sized and placed as the text.</b> Each face is built so its em is the font's em
/// (<see cref="FontVerticalMetrics"/>), on the font's baseline, as ImGui places a merged face.</item>
/// </list>
///
/// Thread-safe: symbols are taken on the render thread and read on the atlases' build threads,
/// through snapshots replaced whole.
/// </summary>
internal sealed class SymbolFallback
{
    /// <summary>The most symbols one game session draws through the fallback (see the type doc).</summary>
    internal const int MaxSymbols = 48;

    /// <summary>
    /// Where the fallback looks, inclusive: Arrows to Miscellaneous Symbols and Arrows (U+2190 to
    /// U+2BFF) and the Yijing hexagrams. The faces draw 1,455 of these 2,736 codepoints: all of
    /// Enclosed Alphanumerics, Geometric Shapes, Miscellaneous Symbols, Braille and the hexagrams,
    /// nearly all Dingbats and Miscellaneous Symbols and Arrows, about half of Miscellaneous
    /// Technical, but only 23 of the 112 Arrows, almost no mathematical operators or supplemental
    /// arrows, and no box drawing or block elements; the rest stays "?" unless the text's own font
    /// has it. Letters, punctuation and digits are the font's own business, and the faces' Latin is
    /// left out of them.
    /// </summary>
    internal static readonly (int First, int Last)[] Blocks = [(0x2190, 0x2BFF), (0x4DC0, 0x4DFF)];

    /// <summary>The symbol faces' embedded resources (AetherFrame.csproj), in the order merged: the first that has a symbol draws it.</summary>
    internal static readonly string[] FaceResources =
    [
        "AetherFrame.Fonts.Symbols.NotoSansSymbols2-Regular.ttf",
        "AetherFrame.Fonts.Symbols.NotoSansSymbols-Regular.ttf",
    ];

    private readonly Lazy<Face[]> faces;
    private readonly object gate = new();

    // Replaced whole under the gate, read without it.
    private int[] held = [];
    private IReadOnlyList<SymbolMerge> merges = [];

    /// <param name="load">The bytes of an embedded face, by resource name (<see cref="FaceResources"/>);
    /// called at most once per face, the first time a symbol is looked up.</param>
    internal SymbolFallback(Func<string, byte[]> load)
    {
        ArgumentNullException.ThrowIfNull(load);
        faces = new Lazy<Face[]>(() => FaceResources.Select(resource => Face.Read(resource, load(resource))).ToArray(), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>How many symbols the session has taken.</summary>
    internal int Count => Volatile.Read(ref held).Length;

    /// <summary>
    /// What to merge into a font for the symbols taken so far: each face with what it draws of
    /// them. Empty while nothing is taken, so a font builds exactly as it would without the
    /// fallback. Any thread; the list and its arrays never change.
    /// </summary>
    internal IReadOnlyList<SymbolMerge> Merges => Volatile.Read(ref merges);

    /// <summary>Whether <paramref name="codepoint"/> is in <see cref="Blocks"/> (cheap: no face is read).</summary>
    internal static bool InBlocks(int codepoint)
    {
        foreach (var (first, last) in Blocks)
        {
            if (codepoint >= first && codepoint <= last)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether <paramref name="text"/> has any character in <see cref="Blocks"/>.</summary>
    internal static bool AnyInBlocks(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (c >= 0x2190 && InBlocks(c))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a symbol face draws <paramref name="codepoint"/>.</summary>
    internal bool CanDraw(int codepoint) => InBlocks(codepoint) && faces.Value.Any(face => face.Maps(codepoint));

    /// <summary>Whether <paramref name="codepoint"/> has been taken, so fonts are (or are being) built with it.</summary>
    internal bool Holds(int codepoint) => Array.BinarySearch(Volatile.Read(ref held), codepoint) >= 0;

    /// <summary>Whether a font without <paramref name="codepoint"/> draws it all the same: it is taken, or a face draws it and there is room to take it.</summary>
    internal bool CanStillDraw(int codepoint) => Holds(codepoint) || (Count < MaxSymbols && CanDraw(codepoint));

    /// <summary>Takes every symbol in <paramref name="text"/> the faces draw (see <see cref="Take(int)"/>); true when any was new.</summary>
    internal bool Take(ReadOnlySpan<char> text)
    {
        // Measuring asks every frame while a symbol is on its way: nothing is allocated unless one is new.
        if (Count >= MaxSymbols || !AnyInBlocks(text))
        {
            return false;
        }

        List<int>? added = null;
        foreach (var c in text)
        {
            if (InBlocks(c) && !Holds(c) && added?.Contains(c) != true && CanDraw(c))
            {
                (added ??= new List<int>()).Add(c);
            }
        }

        return added is not null && Add(added);
    }

    /// <summary>
    /// Takes <paramref name="codepoint"/>, so every font is built with it from now on, when a
    /// face draws it and fewer than <see cref="MaxSymbols"/> are taken; true when it was new (the
    /// caller then has the fonts built again).
    /// </summary>
    internal bool Take(int codepoint) => InBlocks(codepoint) && !Holds(codepoint) && CanDraw(codepoint) && Add([codepoint]);

    /// <summary>One line for <c>/af fonts</c>.</summary>
    internal string Describe()
    {
        var count = Count;
        return count == 0
            ? "No symbols drawn from the fallback yet."
            : string.Create(CultureInfo.InvariantCulture, $"Symbols drawn from the fallback: {count} of {MaxSymbols}{(count >= MaxSymbols ? " (full: any other stays ? until the game restarts)" : string.Empty)}.");
    }

    private bool Add(List<int> codepoints)
    {
        lock (gate)
        {
            var next = new SortedSet<int>(held);
            foreach (var codepoint in codepoints)
            {
                if (next.Count >= MaxSymbols)
                {
                    break;
                }

                next.Add(codepoint);
            }

            if (next.Count == held.Length)
            {
                return false;
            }

            var taken = next.ToArray();
            Volatile.Write(ref merges, BuildMerges(taken));
            Volatile.Write(ref held, taken);
            return true;
        }
    }

    // Each face gets the taken symbols it has and no earlier face has: ImGui would skip those anyway.
    private IReadOnlyList<SymbolMerge> BuildMerges(int[] taken)
    {
        var result = new List<SymbolMerge>();
        var left = new List<int>(taken);
        foreach (var face in faces.Value)
        {
            var mine = left.Where(face.Maps).ToList();
            if (mine.Count == 0)
            {
                continue;
            }

            left.RemoveAll(mine.Contains);
            result.Add(new SymbolMerge(face.Resource, ToGlyphRanges(mine), face.Metrics));
        }

        return result;
    }

    /// <summary>Ascending codepoints as ImGui glyph ranges: inclusive pairs, runs joined, zero-terminated.</summary>
    internal static ushort[] ToGlyphRanges(IReadOnlyList<int> ascending)
    {
        var ranges = new List<ushort>();
        for (var i = 0; i < ascending.Count; i++)
        {
            var first = ascending[i];
            while (i + 1 < ascending.Count && ascending[i + 1] == ascending[i] + 1)
            {
                i++;
            }

            ranges.Add((ushort)first);
            ranges.Add((ushort)ascending[i]);
        }

        ranges.Add(0);
        return ranges.ToArray();
    }

    private sealed class Face
    {
        private readonly HashSet<int> mapped;

        private Face(string resource, FontVerticalMetrics metrics, HashSet<int> mapped)
        {
            Resource = resource;
            Metrics = metrics;
            this.mapped = mapped;
        }

        internal string Resource { get; }

        internal FontVerticalMetrics Metrics { get; }

        internal bool Maps(int codepoint) => mapped.Contains(codepoint);

        internal static Face Read(string resource, byte[] font)
        {
            if (!TrueTypeTables.TryReadVerticalMetrics(font, out var metrics))
            {
                throw new InvalidOperationException($"The symbol face '{resource}' has no readable metrics.");
            }

            var mapped = new HashSet<int>();
            foreach (var (first, last) in Blocks)
            {
                mapped.UnionWith(TrueTypeTables.MappedCodepoints(font, first, last));
            }

            return new Face(resource, metrics, mapped);
        }
    }
}
