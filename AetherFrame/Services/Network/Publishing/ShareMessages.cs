using AetherFrame.Domain.Profiles;

namespace AetherFrame.Services.Network.Publishing;

/// <summary>
/// What the share check, the Sharing window and the progress window say to the player: why a Plate
/// can't be shared, what it leaves out, and why a check couldn't finish. Each message is plain text,
/// drawn unformatted, and names no path, id or label; the window adds the Plate's own words beside
/// it where they belong. Compiled only in the sharing build.
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

    /// <summary>Why something the Plate holds isn't in what would be shared, after its name and a colon.</summary>
    internal static string For(LeftOutReason reason) => reason switch
    {
        LeftOutReason.Empty => "nothing to show",
        LeftOutReason.Transparent => "fully transparent",
        LeftOutReason.OutsideView => "outside what the Plate shows",
        LeftOutReason.ImageMissing => "its image file is missing, so nothing is drawn there",
        _ => "not drawn",
    };

    /// <summary>Why a check couldn't finish; nothing is wrong with the Plate itself.</summary>
    internal static string For(ShareCheckFailure failure) => failure switch
    {
        ShareCheckFailure.PlateUnavailable => "The saved Plate couldn't be read. Check that it opens in My Plates.",
        ShareCheckFailure.FontsLoading => "Its fonts are still loading. Try again in a moment.",
        ShareCheckFailure.PreparationOff => "Image preparation didn't pass its check in this session, so sharing is off until AetherFrame starts again. AetherFrame's log says what failed.",
        ShareCheckFailure.PreparationFailed => "Its images couldn't be prepared. Try again; if it happens again, AetherFrame's log says what failed.",
        ShareCheckFailure.ResolveFailed => "It couldn't be checked. Try again; if it happens again, AetherFrame's log says what failed.",
        ShareCheckFailure.Unloading => "AetherFrame is unloading.",
        _ => "The check couldn't finish.",
    };

    /// <summary>Whether a text of this role holds what AetherFrame filled in from the character (the name, World and Data Center, job and Free Company tag), which a player may not think of as shared.</summary>
    internal static bool IsFromTheCharacter(ProfileElementRole? role) =>
        role is ProfileElementRole.BasicName or ProfileElementRole.BasicWorld or ProfileElementRole.BasicJob or ProfileElementRole.BasicFreeCompany;
}
