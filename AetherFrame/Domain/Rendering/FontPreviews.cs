using System;
using System.Collections.Generic;

namespace AetherFrame.Domain.Rendering;

/// <summary>
/// What the font list shows each family's sample with (issue #116): a short text, drawn in the
/// family's own Regular face at one small tier of <see cref="FontTierPolicy.SizeLadder"/>, chosen from
/// the interface's font size. Pure rules, free of Dalamud.
/// </summary>
internal static class FontPreview
{
    /// <summary>The sample every family shows: the same words in each, so faces are compared like for like.
    /// Capitals, ascenders and a descender, in a phrase every player knows.</summary>
    internal const string Sample = "Warrior of Light";

    /// <summary>
    /// The glyphs a preview face is built with, in ImGui's format: Basic Latin, which holds the sample.
    /// A Plate's font keeps every glyph its face maps (<see cref="FontTierPolicy.GlyphRanges"/>); a
    /// preview needs only these, so it builds in a fraction of the time and space. The array is shared
    /// and must not be modified.
    /// </summary>
    internal static readonly ushort[] GlyphRanges = [0x0020, 0x007E, 0];

    /// <summary>The sample's size, as a multiple of the interface font's: large enough that a script
    /// face's lowercase can be read, small enough that a screen holds a dozen rows or more.</summary>
    internal const float SizeToInterface = 1.75f;

    /// <summary>The smallest tier a preview is built at, whatever the interface's scale.</summary>
    internal const float SmallestSize = 20f;

    /// <summary>The largest tier a preview is built at, whatever the interface's scale.</summary>
    internal const float LargestSize = 56f;

    /// <summary>
    /// The ladder tier previews are built and drawn at for an interface font of
    /// <paramref name="interfaceFontSize"/> pixels: the one nearest <see cref="SizeToInterface"/> times
    /// it, within <see cref="SmallestSize"/> and <see cref="LargestSize"/>. A sample is drawn at exactly
    /// its tier's size, so it is never scaled and stays crisp; one tier for every family keeps them
    /// comparable, and the interface's scale changes it only in steps.
    /// </summary>
    internal static int TierIndex(float interfaceFontSize)
    {
        var wanted = Math.Clamp(float.IsFinite(interfaceFontSize) ? interfaceFontSize * SizeToInterface : 0f, SmallestSize, LargestSize);
        var best = 0;
        for (var i = 1; i < FontTierPolicy.SizeLadder.Count; i++)
        {
            if (Math.Abs(FontTierPolicy.SizeLadder[i] - wanted) < Math.Abs(FontTierPolicy.SizeLadder[best] - wanted))
            {
                best = i;
            }
        }

        return best;
    }

    /// <summary>The size of <see cref="TierIndex"/>'s tier, in pixels.</summary>
    internal static float Size(float interfaceFontSize) => FontTierPolicy.SizeLadder[TierIndex(interfaceFontSize)];
}

/// <summary>
/// The font list's preview faces, free of Dalamud (issue #116): one face per family, its Regular
/// style at one small tier, built only with <see cref="FontPreview.GlyphRanges"/> and only for the
/// rows on screen. Each preview has an atlas of its own, as each of the Plate's font families has
/// (<see cref="FontHandleCache{TAtlas, THandle}"/>, issue #117): building one rasterizes that one
/// face, never another family's, and never any of the Plate's own fonts, which live in other atlases
/// altogether.
///
/// <list type="bullet">
/// <item><b>Paced.</b> At most <see cref="MaxStartsPerFrame"/> previews start building a frame, and
/// at most <see cref="MaxBuilding"/> build at once. A row waiting its turn draws its sample in the
/// interface font, and asks again the next frame while it is still on screen, so scrolling fast
/// through the list starts only what stays in view, never a burst of builds.</item>
/// <item><b>Bounded.</b> A preview drawn within <see cref="InUseMilliseconds"/> is kept. Beyond those,
/// at most <see cref="MaxKept"/> are: the least recently drawn go first, checked once a frame while
/// the list is open, so the previews held never outgrow a few screens of the list, however long or
/// fast it is browsed.</item>
/// <item><b>Kept between openings.</b> Nothing is let go when the list closes, so reopening it,
/// where it was (issue #114), shows its previews at once. They go with the font service.</item>
/// </list>
///
/// The framework thread only.
/// </summary>
internal sealed class FontPreviews<TAtlas, THandle> : IDisposable
    where TAtlas : class
    where THandle : class
{
    /// <summary>The most previews started in one frame.</summary>
    internal const int MaxStartsPerFrame = 2;

    /// <summary>The most previews building at once.</summary>
    internal const int MaxBuilding = 4;

    /// <summary>The most previews kept beyond those in use.</summary>
    internal const int MaxKept = 32;

    /// <summary>A preview drawn this recently is in use (a row on screen, or just scrolled past), and kept.</summary>
    internal const long InUseMilliseconds = 2000;

    /// <summary>A build not finished after this long (a font that failed to load) stops counting
    /// towards <see cref="MaxBuilding"/>, so it can never hold the other previews back. Its row keeps
    /// the interface font.</summary>
    internal const long BuildTimeoutMilliseconds = 10_000;

    private readonly IFontAtlasBackend<TAtlas, THandle> backend;
    private readonly Func<long> clock;
    private readonly Func<long> frame;
    private readonly Dictionary<string, Preview> previews = new(StringComparer.Ordinal);
    private readonly List<Preview> building = new(MaxBuilding);
    private long currentFrame = long.MinValue;
    private int startedThisFrame;
    private bool disposed;

    /// <param name="backend">The font system: Dalamud's atlases in the plugin, a fake in tests.</param>
    /// <param name="clock">Milliseconds, ever increasing (Environment.TickCount64 in the plugin).</param>
    /// <param name="frame">The frame being drawn (ImGui's frame count in the plugin).</param>
    internal FontPreviews(IFontAtlasBackend<TAtlas, THandle> backend, Func<long> clock, Func<long> frame)
    {
        this.backend = backend;
        this.clock = clock;
        this.frame = frame;
    }

    /// <summary>How many previews are held, built or building.</summary>
    internal int Count => previews.Count;

    /// <summary>How many previews are building now.</summary>
    internal int Building
    {
        get
        {
            PruneBuilding(clock());
            return building.Count;
        }
    }

    /// <summary>
    /// The preview face of <paramref name="familyId"/> at <paramref name="sizePx"/> (a ladder tier,
    /// <see cref="FontPreview.TierIndex"/>), marking it drawn: null while it builds or waits its turn.
    /// Starts building it when its turn comes. Call it only for a row on screen.
    /// </summary>
    internal THandle? Get(string? familyId, float sizePx)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var now = clock();
        NewFrame(now);

        var id = FontTierPolicy.ResolveFamilyId(familyId);
        if (previews.TryGetValue(id, out var preview) && preview.SizePx.Equals(sizePx))
        {
            preview.LastUsed = now;
            return backend.IsAvailable(preview.Handle) ? preview.Handle : null;
        }

        if (!MayStart(now))
        {
            // The interface's scale changed: the old size stands in until the new one's turn comes.
            if (preview is not null)
            {
                preview.LastUsed = now;
                return backend.IsAvailable(preview.Handle) ? preview.Handle : null;
            }

            return null;
        }

        if (preview is not null)
        {
            Remove(preview);
        }

        Trim(now, room: 1);
        startedThisFrame++;
        var atlas = backend.CreateAtlas(id);
        var started = new Preview(id, atlas, backend.CreateHandle(atlas, new FontCacheKey(id, sizePx, false, false)), sizePx, now);
        previews[id] = started;
        if (!backend.IsAvailable(started.Handle))
        {
            building.Add(started);
            return null;
        }

        return started.Handle;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        foreach (var preview in previews.Values)
        {
            backend.DisposeHandle(preview.Handle);
            backend.DisposeAtlas(preview.Atlas);
        }

        previews.Clear();
        building.Clear();
    }

    /// <summary>Once a frame: a new allowance of starts, and the previews beyond the bound let go.</summary>
    private void NewFrame(long now)
    {
        var current = frame();
        if (current == currentFrame)
        {
            return;
        }

        currentFrame = current;
        startedThisFrame = 0;
        Trim(now, room: 0);
    }

    private bool MayStart(long now)
    {
        if (startedThisFrame >= MaxStartsPerFrame)
        {
            return false;
        }

        PruneBuilding(now);
        return building.Count < MaxBuilding;
    }

    /// <summary>Forgets the builds that finished, were let go, or ran out of time.</summary>
    private void PruneBuilding(long now)
    {
        for (var i = building.Count - 1; i >= 0; i--)
        {
            var preview = building[i];
            if (preview.Removed || backend.IsAvailable(preview.Handle) || now - preview.Started >= BuildTimeoutMilliseconds)
            {
                building.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Lets go of the least recently drawn previews not in use until at most <see cref="MaxKept"/>
    /// minus <paramref name="room"/> are held, or only previews in use are left.
    /// </summary>
    private void Trim(long now, int room)
    {
        while (previews.Count > MaxKept - room)
        {
            Preview? oldest = null;
            foreach (var preview in previews.Values)
            {
                if (now - preview.LastUsed >= InUseMilliseconds && (oldest is null || preview.LastUsed < oldest.LastUsed))
                {
                    oldest = preview;
                }
            }

            if (oldest is null)
            {
                return;
            }

            Remove(oldest);
        }
    }

    /// <summary>A preview's face goes, then its atlas, as the Plate's fonts do.</summary>
    private void Remove(Preview preview)
    {
        previews.Remove(preview.FamilyId);
        preview.Removed = true;
        backend.DisposeHandle(preview.Handle);
        backend.DisposeAtlas(preview.Atlas);
    }

    private sealed class Preview(string familyId, TAtlas atlas, THandle handle, float sizePx, long started)
    {
        public string FamilyId { get; } = familyId;

        public TAtlas Atlas { get; } = atlas;

        public THandle Handle { get; } = handle;

        public float SizePx { get; } = sizePx;

        public long Started { get; } = started;

        public long LastUsed { get; set; } = started;

        public bool Removed { get; set; }
    }
}
