using System;
using System.Collections.Generic;
using System.Threading;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Profiles;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Remote;

namespace AetherFrame.Services.Network.Publishing;

/// <summary>
/// What resolving a Plate needs from the renderer and the managed images. Each call may answer
/// "not ready" (a font still being built), which fails the resolve: nothing falls back to a value
/// the renderer wouldn't draw with. The plugin answers with the renderer's own measurements, on
/// the framework thread; tests answer with fixed values.
/// </summary>
internal interface IPlateMeasurements
{
    /// <summary>A text's natural single-line width at its own size, as the renderer measures it; false when its font isn't built yet.</summary>
    bool TryMeasureNaturalWidth(TextProfileElement element, out float width);

    /// <summary>The text drawn instead of an element's own (the Favorite Jobs display), or null for its own; false when it can't be decided yet.</summary>
    bool TryGetDisplayOverride(ProfileDocument plate, TextProfileElement element, out string? display);

    /// <summary>A managed image's size in pixels, from its file's header; false when the file is missing or unreadable.</summary>
    bool TryGetImageSize(Guid image, out int width, out int height);
}

/// <summary>A rectangle of an image's pixels.</summary>
internal readonly record struct PixelWindow(int X, int Y, int Width, int Height)
{
    internal static PixelWindow Whole(int width, int height) => new(0, 0, width, height);
}

/// <summary>
/// A window of a managed image that the Plate draws, and, once resolved, a prepared copy it needs:
/// cropped to that window and no more. An image drawn with several windows needs a copy of each,
/// except a window inside another, which is drawn from that one's copy (every window is centred in
/// its image, so Fill on the larger copy gives the smaller window back).
/// <paramref name="SourceWidth"/> by <paramref name="SourceHeight"/> is the size the window was
/// computed against (the file's header): preparation refuses a decoded image of any other size,
/// rather than crop a window that isn't the one drawn.
/// </summary>
internal readonly record struct ImageRequirement(Guid Image, PixelWindow Window, int SourceWidth, int SourceHeight);

/// <summary>A prepared copy (N2-6b): its declaration (a fresh asset id, its digest, format and size) and its exact bytes.</summary>
internal sealed record PreparedImage(ImageReference Reference, ReadOnlyMemory<byte> Bytes);

/// <summary>Why a required window of an image has no prepared copy.</summary>
internal enum ImageUnavailableReason
{
    /// <summary>The managed file is missing or can't be decoded: the renderer draws nothing, or a placeholder, there.</summary>
    Missing,

    /// <summary>Over the limits a shared image has (decision I1: 8,192 pixels a side, 20,000,000 in all, 8 MiB prepared).</summary>
    TooLarge,

    /// <summary>Decoded at another size than its header gave, so the window drawn isn't the window computed.</summary>
    SizeChanged,

    /// <summary>The prepared copy failed a check every shared image passes (its container, section 8.2.1, or its own declaration).</summary>
    Unshareable,
}

/// <summary>What preparing one required window produced: its copy, or why there is none. Every requirement has one.</summary>
internal sealed class ImagePreparation
{
    private ImagePreparation(PreparedImage? copy, ImageUnavailableReason reason)
    {
        Copy = copy;
        Reason = reason;
    }

    /// <summary>The prepared copy; null when there is none, and <see cref="Reason"/> says why.</summary>
    internal PreparedImage? Copy { get; }

    /// <summary>Why there is no copy; meaningless when <see cref="Copy"/> is set.</summary>
    internal ImageUnavailableReason Reason { get; }

    internal static ImagePreparation Prepared(PreparedImage copy) => new(copy ?? throw new ArgumentNullException(nameof(copy)), default);

    internal static ImagePreparation Unavailable(ImageUnavailableReason reason) => new(null, reason);
}

/// <summary>One step of what the local renderer visibly draws for a saved Plate, in paint order.</summary>
internal abstract record ResolvedStep;

/// <summary>
/// A text element, with the text the renderer draws for it (its affixes, or the Favorite Jobs
/// display), and the size, wrap and auto fit it is drawn with: its own, or for the Basic name the
/// ones <c>BasicNameFit</c> resolved (<paramref name="SizeBaked"/>).
/// </summary>
internal sealed record ResolvedText(TextProfileElement Element, string Text, float FontSize, bool Wrap, bool AutoFit, bool SizeBaked) : ResolvedStep;

/// <summary>An image element, and the prepared copy it draws from: its own window, or one holding it.</summary>
internal sealed record ResolvedImage(ImageProfileElement Element, ImageRequirement Image) : ResolvedStep;

/// <summary>
/// One primitive of <paramref name="Component"/>'s placement: a filled quad or triangle, the
/// component's image (<paramref name="Image"/>, the copy of the whole of it), or its bundled art
/// (<paramref name="Art"/>, the art's id).
/// </summary>
internal sealed record ResolvedShape(ComponentPrimitive Primitive, ImageRequirement? Image, string? Art, PlateComponent? Component = null) : ResolvedStep;

/// <summary>
/// The background as it draws: null when it draws nothing at all; <paramref name="NoBase"/> when
/// Image mode has no image, so only its pattern draws; and the prepared copy of its image it draws
/// from.
/// </summary>
internal sealed record ResolvedBackground(ProfileBackground? Background, ImageRequirement? Image, bool NoBase)
{
    internal static readonly ResolvedBackground Nothing = new(null, null, NoBase: false);
}

/// <summary>Why something the Plate holds isn't in what is shared.</summary>
internal enum LeftOutReason
{
    /// <summary>A text with nothing to show.</summary>
    Empty,

    /// <summary>A text or image drawn fully transparent: at an alpha the renderer turns into 0.</summary>
    Transparent,

    /// <summary>Wholly outside what the Profile View shows (the canvas and every component's painted bounds).</summary>
    OutsideView,

    /// <summary>A component's image is missing, so it draws nothing.</summary>
    ImageMissing,
}

/// <summary>An element or component left out of what is shared, and why; the consent screen lists these.</summary>
internal readonly record struct LeftOutItem(ProfileElement? Element, PlateComponent? Component, LeftOutReason Reason);

/// <summary>
/// A saved Plate as the local renderer visibly draws it: its canvas and background, the steps in
/// paint order, what was left out, the prepared copies those steps draw from (each once, in the
/// order first drawn), and the reasons found so far that it can't be shared.
/// </summary>
internal sealed class ResolvedPlate
{
    internal ResolvedPlate(
        Guid plateId, string name, float canvasWidth, float canvasHeight, ResolvedBackground background,
        IReadOnlyList<ResolvedStep> steps, IReadOnlyList<LeftOutItem> leftOut, IReadOnlyList<ImageRequirement> requirements, IReadOnlyList<PlateSnapshotProblem> problems)
    {
        PlateId = plateId;
        Name = name;
        CanvasWidth = canvasWidth;
        CanvasHeight = canvasHeight;
        Background = background;
        Steps = steps;
        LeftOut = leftOut;
        Requirements = requirements;
        Problems = problems;
    }

    /// <summary>The local Plate's id: kept for the publication index, never shared.</summary>
    internal Guid PlateId { get; }

    internal string Name { get; }

    internal float CanvasWidth { get; }

    internal float CanvasHeight { get; }

    internal ResolvedBackground Background { get; }

    internal IReadOnlyList<ResolvedStep> Steps { get; }

    internal IReadOnlyList<LeftOutItem> LeftOut { get; }

    /// <summary>The prepared copies needed, each once: every window of a managed image that is drawn, bar those inside another.</summary>
    internal IReadOnlyList<ImageRequirement> Requirements { get; }

    internal IReadOnlyList<PlateSnapshotProblem> Problems { get; }
}

/// <summary>Why a saved Plate can't be shared as it is. Each is fixed by changing the Plate: nothing is ever clamped, trimmed or dropped to get past one.</summary>
internal enum PlateSnapshotRefusal
{
    /// <summary>The Plate's name breaks the shared-name rule (decision D4).</summary>
    Name,

    /// <summary>More items than a shared Plate can hold.</summary>
    TooManyItems,

    /// <summary>More image copies than a shared Plate can hold: each window of an image drawn is a copy, bar one inside another.</summary>
    TooManyImages,

    /// <summary>More image bytes in all than a shared Plate can hold.</summary>
    TooManyImageBytes,

    /// <summary>More image pixels in all than a shared Plate can hold.</summary>
    TooManyImagePixels,

    /// <summary>More text in all than a shared Plate can hold.</summary>
    TooMuchText,

    /// <summary>One text is longer than a shared text can be.</summary>
    TextTooLong,

    /// <summary>A text holds U+0000 or a broken surrogate, which no shared text can.</summary>
    TextUnshareable,

    /// <summary>A background gradient has a colour component outside 0 to 1, which the renderer blends before it resolves.</summary>
    GradientColor,

    /// <summary>A value is outside what a shared Plate can express.</summary>
    ValueOutOfRange,

    /// <summary>An element of a kind this build doesn't know.</summary>
    UnknownElement,

    /// <summary>
    /// Something a newer AetherFrame made, which this build keeps but can't show as its maker saw
    /// it: an element or component it can't read, a component of a kind or definition it doesn't
    /// know, or a setting it doesn't recognize on something drawn.
    /// </summary>
    MadeByNewerVersion,

    /// <summary>An image element's image is missing.</summary>
    ImageMissing,

    /// <summary>The background's image is missing.</summary>
    BackgroundImageMissing,

    /// <summary>An image the Plate draws is over the limits a shared image has.</summary>
    ImageTooLarge,

    /// <summary>An image the Plate draws decoded at another size than its file says, so what is drawn can't be told.</summary>
    ImageSizeChanged,

    /// <summary>An image the Plate draws couldn't be prepared as every shared image must be.</summary>
    ImageUnshareable,
}

/// <summary>
/// One reason a Plate can't be shared, and what it is about: an element, a component, or the
/// background (<paramref name="Background"/>); the Plate as a whole when none of them.
/// </summary>
internal readonly record struct PlateSnapshotProblem(PlateSnapshotRefusal Refusal, ProfileElement? Element, PlateComponent? Component = null, bool Background = false);

/// <summary>
/// Everything a Plate's snapshot will say, resolved, prepared and checked before the consent
/// screen opens: the consent screen shows exactly this, and the commit signs exactly this, adding
/// only the profile id, the revision id and the time. Nothing is read from the Plate again.
/// </summary>
internal sealed class SnapshotCandidate
{
    private int claimed;

    internal SnapshotCandidate(
        Guid plateId, string name, int canvasWidth, int canvasHeight, LayoutBackground background,
        IReadOnlyList<LayoutItem> items, IReadOnlyList<ProfileElementRole?> roles, IReadOnlyList<ImageReference> images,
        IReadOnlyList<ReadOnlyMemory<byte>> imageBytes, IReadOnlyList<LeftOutItem> leftOut)
    {
        PlateId = plateId;
        Name = name;
        CanvasWidth = canvasWidth;
        CanvasHeight = canvasHeight;
        Background = background;
        Items = items;
        Roles = roles;
        Images = images;
        ImageBytes = imageBytes;
        LeftOut = leftOut;
    }

    /// <summary>The local Plate's id, for the publication index only.</summary>
    internal Guid PlateId { get; }

    /// <summary>The name shared with the snapshot, which no item draws.</summary>
    internal string Name { get; }

    internal int CanvasWidth { get; }

    internal int CanvasHeight { get; }

    internal LayoutBackground Background { get; }

    /// <summary>The items, in paint order.</summary>
    internal IReadOnlyList<LayoutItem> Items { get; }

    /// <summary>Each item's local role (null for a component's shapes), so the consent screen can flag text the game filled in.</summary>
    internal IReadOnlyList<ProfileElementRole?> Roles { get; }

    /// <summary>The prepared copies the items and the background draw, each once.</summary>
    internal IReadOnlyList<ImageReference> Images { get; }

    /// <summary>The exact bytes of each prepared copy, in the order of <see cref="Images"/>.</summary>
    internal IReadOnlyList<ReadOnlyMemory<byte>> ImageBytes { get; }

    /// <summary>What the Plate holds that isn't shared, and why.</summary>
    internal IReadOnlyList<LeftOutItem> LeftOut { get; }

    /// <summary>The snapshot this candidate is, under the given ids and time (the commit's own).</summary>
    internal ProfileLayoutSnapshot ToSnapshot(ProfileId profileId, RevisionId revisionId, long createdAtUnixSeconds) =>
        new(profileId, revisionId, createdAtUnixSeconds, Name, CanvasWidth, CanvasHeight, Background, Items, Images);

    /// <summary>
    /// Claims the candidate for its one signing attempt: true the first time, false ever after, even
    /// when that attempt failed. A signed revision takes its asset ids for good (N1), so a candidate
    /// is signed at most once, and every result past the claim builds a new candidate. That keeps
    /// two revisions from sharing an asset id only while every candidate has ids of its own: N2-6c's
    /// second part draws them afresh for each candidate it builds.
    /// </summary>
    internal bool TryClaimForSigning() => Interlocked.Exchange(ref claimed, 1) == 0;
}

/// <summary>A candidate built from a saved Plate, or the reasons it can't be built. Exactly one of the two.</summary>
internal sealed class SnapshotCandidateResult
{
    private SnapshotCandidateResult(SnapshotCandidate? candidate, IReadOnlyList<PlateSnapshotProblem> problems)
    {
        Candidate = candidate;
        Problems = problems;
    }

    /// <summary>The candidate; null when <see cref="Problems"/> holds anything.</summary>
    internal SnapshotCandidate? Candidate { get; }

    /// <summary>Every reason the Plate can't be shared as it is; empty when <see cref="Candidate"/> is built.</summary>
    internal IReadOnlyList<PlateSnapshotProblem> Problems { get; }

    internal static SnapshotCandidateResult Built(SnapshotCandidate candidate) => new(candidate, Array.Empty<PlateSnapshotProblem>());

    internal static SnapshotCandidateResult Refused(IReadOnlyList<PlateSnapshotProblem> problems) => new(null, problems);
}
