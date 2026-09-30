using AetherFrame.Domain.Profiles;

namespace AetherFrame.Services.Network.Publishing;

/// <summary>
/// What the preview's share check says to the player: why a Plate can't be shared, what it leaves
/// out, why a check couldn't finish, and what signing came to. Each message is plain text, drawn
/// unformatted, and names no path, id or label; the window adds the Plate's or the persona's own
/// words beside it where they belong. The consent screen (N2-9) builds on these. Compiled only in
/// the networking preview flavour.
/// </summary>
internal static class ShareMessages
{
    /// <summary>Why a Plate can't be shared as it is, and what to change.</summary>
    internal static string For(PlateSnapshotRefusal refusal) => refusal switch
    {
        PlateSnapshotRefusal.Name => "Its name can't be shared: a shared name has 1 to 64 characters, with no line breaks or invisible characters. Rename the Plate.",
        PlateSnapshotRefusal.TooManyItems => "It draws more than 2,048 items, the most a shared Plate holds.",
        PlateSnapshotRefusal.TooManyImages => "It draws more than 8 images. Each part of an image drawn in its own way counts as one.",
        PlateSnapshotRefusal.TooManyImageBytes => "Its images come to more than 40 MiB, the most a shared Plate holds.",
        PlateSnapshotRefusal.TooManyImagePixels => "Its images have more than 33,554,432 pixels in all, the most a shared Plate holds.",
        PlateSnapshotRefusal.TooMuchText => "Its texts hold more than 32,000 characters in all, the most a shared Plate holds.",
        PlateSnapshotRefusal.TextTooLong => "A text is longer than 2,048 characters, the most a shared text holds.",
        PlateSnapshotRefusal.TextUnshareable => "A text holds a character no shared text can: an empty character or a broken pair.",
        PlateSnapshotRefusal.GradientColor => "The background's gradient uses a colour outside the range a shared Plate can express.",
        PlateSnapshotRefusal.ValueOutOfRange => "A value is outside what a shared Plate can express.",
        PlateSnapshotRefusal.UnknownElement => "It holds an element of a kind this AetherFrame doesn't know.",
        PlateSnapshotRefusal.MadeByNewerVersion => "It holds something a newer AetherFrame made, which this one can't show as its maker saw it. Update AetherFrame to share it.",
        PlateSnapshotRefusal.ImageMissing => "An image's file is missing, so a placeholder would be drawn in its place.",
        PlateSnapshotRefusal.BackgroundImageMissing => "The background's image file is missing, so a placeholder would be drawn in its place.",
        PlateSnapshotRefusal.ImageTooLarge => "An image is over the limits a shared image has: 8,192 pixels a side, 20,000,000 pixels in all, and 8 MiB once prepared.",
        PlateSnapshotRefusal.ImageSizeChanged => "An image decodes at another size than its file says, as an animation's first frame can, so what is drawn can't be told.",
        PlateSnapshotRefusal.ImageUnshareable => "An image couldn't be prepared as every shared image must be. Black-and-white images can't be shared yet.",
        _ => "It holds something that can't be shared.",
    };

    /// <summary>Why something the Plate holds isn't in what would be shared.</summary>
    internal static string For(LeftOutReason reason) => reason switch
    {
        LeftOutReason.Empty => "has nothing to show",
        LeftOutReason.Transparent => "is fully transparent",
        LeftOutReason.OutsideView => "is outside what the Plate shows",
        LeftOutReason.ImageMissing => "has no image file, so nothing is drawn there",
        _ => "isn't drawn",
    };

    /// <summary>Why a check couldn't finish; nothing is wrong with the Plate itself.</summary>
    internal static string For(ShareCheckFailure failure) => failure switch
    {
        ShareCheckFailure.PlateUnavailable => "The saved Plate couldn't be read. Check that it opens in My Plates.",
        ShareCheckFailure.FontsLoading => "Its fonts are still loading. Try again in a moment.",
        ShareCheckFailure.PreparationOff => "Image preparation didn't pass its check in this session, so sharing is off until AetherFrame starts again. AetherFrame's log says what failed.",
        ShareCheckFailure.PreparationFailed => "Its images couldn't be prepared. Try again; if it happens again, AetherFrame's log says what failed.",
        ShareCheckFailure.Unloading => "AetherFrame is unloading.",
        _ => "The check couldn't finish.",
    };

    /// <summary>What signing came to. <see cref="PublishResult.Stored"/> is followed by the window's own note that nothing is sent yet.</summary>
    internal static string For(PublishResult result) => result switch
    {
        PublishResult.Stored => "Signed and kept on this PC.",
        PublishResult.IndexUnreadable => "This persona's list of what it shared can't be read, so sharing is off for it. The file is left exactly as it was.",
        PublishResult.IndexNewerVersion => "This persona's list of what it shared was saved by a newer AetherFrame, so sharing is off for it. Update AetherFrame; the file is left exactly as it was.",
        PublishResult.IndexFull => "This persona has shared as many Plates as it can from here (256).",
        PublishResult.CandidateUsed => "This check was used for a signature already. Check again to sign.",
        PublishResult.ActivePersonaChanged => "The persona in use isn't the one shown any more. Nothing was signed; check again.",
        PublishResult.KeyUnavailable => "This persona's key can't be opened on this PC now. The Personas window says more.",
        PublishResult.NotAcknowledged => "First acknowledge, in the Personas window, what losing this persona's key means.",
        PublishResult.SigningFailed => "Signing failed, so nothing was kept. Check that your PC's clock is right, then try again.",
        PublishResult.NotAsShown => "What would be kept isn't exactly what was checked, so nothing was kept. Check again.",
        PublishResult.OutboxFull => "This persona has 128 MiB of signed Plates waiting to be sent, the most it keeps.",
        PublishResult.NotSaved => "The signed Plate couldn't be saved. Nothing will be sent that wasn't; try again.",
        _ => "Signing didn't finish.",
    };

    /// <summary>Why a persona's list of what it shared couldn't be used.</summary>
    internal static string For(PublicationLoadResult result) => result switch
    {
        PublicationLoadResult.Loaded => string.Empty,
        PublicationLoadResult.IndexUnreadable => "This persona's list of what it shared can't be read, so sharing and unsharing are off for it. The file is left exactly as it was.",
        PublicationLoadResult.IndexNewerVersion => "This persona's list of what it shared was saved by a newer AetherFrame. Update AetherFrame; the file is left exactly as it was.",
        _ => "This persona's list of what it shared couldn't be used.",
    };

    /// <summary>Where one of a persona's profiles stands, for its list.</summary>
    internal static string For(PublicationState state, OutboxState outbox) => (state, outbox) switch
    {
        (_, OutboxState.NotStored) => "its latest signing wasn't stored: share it again",
        (PublicationState.Pending, _) => "signed, waiting to be sent",
        (PublicationState.Published, OutboxState.Waiting) => "shared; an update is waiting to be sent",
        (PublicationState.Published, _) => "shared",
        (PublicationState.Retracting, _) => "being unshared",
        _ => "unknown",
    };

    /// <summary>Whether a text of this role holds what AetherFrame filled in from the character (the name, World and Data Center, job and Free Company tag), which a player may not think of as shared.</summary>
    internal static bool IsFromTheCharacter(ProfileElementRole? role) =>
        role is ProfileElementRole.BasicName or ProfileElementRole.BasicWorld or ProfileElementRole.BasicJob or ProfileElementRole.BasicFreeCompany;
}
