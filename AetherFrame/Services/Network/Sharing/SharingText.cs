using System;

namespace AetherFrame.Services.Network.Sharing;

/// <summary>
/// The words of the sharing window (N2-9b): the consent a character's opting in asks for (decisions
/// C3, C7 and K4 as C9 restates it), the Lodestone steps (C2), and what each outcome means. None of
/// it holds a name, a World, a code or an id: the window draws those on their own, unformatted.
/// </summary>
internal static class SharingText
{
    internal const string Intro =
        "Sharing lets players who use AetherFrame, and have turned sharing on, view each other's Active Plates, like the game's Adventure Plates. It's off until you turn it on, one character at a time.";

    internal const string ConsentTitle = "Before you turn sharing on for this character";

    /// <summary>What turning sharing on means, one statement each (C3, C7, and K4 as C9 restates it).</summary>
    internal static readonly string[] Consent =
    [
        "Other players who have turned sharing on can view this character's Active Plate as a picture: by right-clicking the character in game, or by searching for its name and World. They never get the Plate itself, its Template or your original images.",
        "Once sharing is on, saving this character's Active Plate shares the new version without asking again. Before a Plate is shared for the first time, you'll see exactly what will be sent. My Plates marks the shared Plate, and you can turn sharing off at any time.",
        "Turning sharing off deletes everything the server holds for this character: its Plate, its images and its check.",
        "Anyone who has turned sharing on can find out, from this character's name, that its player uses AetherFrame, and so Dalamud.",
        "The server keeps this character's Lodestone id, name and World, a key that proves the character is yours, and your latest shared Plate. It sees the network address of anyone who shares or views, and doesn't store it. It keeps no record of who viewed whom.",
        "The key stays on this PC. If you share this character from another PC, or after reinstalling Windows, checking it again there moves sharing to it, and what this PC shared is deleted.",
        "To prove the character is yours, you'll paste a short code into its Lodestone profile. While the code is there, anyone reading the profile can see that you use AetherFrame, so delete it once the check passes.",
    ];

    internal const string Agree = "I understand, and I want to share this character's Active Plate.";

    internal const string TurnOn = "Turn on sharing";

    internal const string CodeStepCopy = "1. Copy this code:";

    internal const string CodeWarning = "Only ever paste a code your own AetherFrame shows you here.";

    internal const string CodeStepPaste =
        "2. Sign in to the Lodestone, open your character's Character Profile in its settings, paste the code anywhere in it, and save.";

    internal const string CodeStepAddress = "3. Paste the address of your character's Lodestone page here, then press Check:";

    internal const string AddressHint = "https://na.finalfantasyxiv.com/lodestone/character/12345678/";

    internal const string AddressInvalid = "That isn't a Lodestone character page's address. Open your character's page on the Lodestone and copy the address from your browser.";

    internal const string CodeLife = "The code works once, for an hour. If a check doesn't pass, you can fix the profile and check again.";

    internal const string NoCodeYet = "Get a code to start the Lodestone check.";

    internal const string SharedLine = "Sharing is on for this character, as it appears on the Lodestone:";

    internal const string PublishingLater =
        "Your Active Plate will be shared when you save it. Publishing arrives in the next preview build, so nothing is shared yet.";

    internal const string TurnOffConfirm =
        "Turn off sharing for this character? The server deletes everything it holds for it: its Plate, its images and its check. To share again, you'll need a new Lodestone check.";

    internal const string TurnOffAllConfirm =
        "Turn off sharing for every character on this PC? The server deletes everything it holds for each of them. To share again, each needs a new Lodestone check.";

    internal const string NoCharacter = "Log in to a character to turn sharing on or off for it.";

    internal const string Unreadable =
        "AetherFrame couldn't read which characters you share on this PC, so sharing stays off here until it can. Nothing was changed or deleted.";

    internal const string PersonasUnavailable = "Sharing needs this PC's keys, which aren't available right now:";

    internal const string Starting = "Getting ready...";

    internal const string Busy = "Working...";

    /// <summary>What a notice means, in words.</summary>
    internal static string Notice(SharingNoticeKind kind) => kind switch
    {
        SharingNoticeKind.CodeReady => "Here's your code. Follow the steps below.",
        SharingNoticeKind.CheckPassed => "The check passed: this character is yours on the server. You can now delete the code from your Lodestone profile.",
        SharingNoticeKind.CheckFailed => "The check didn't pass. Make sure the code is saved in the Character Profile of the character whose page you pasted, then check again. It can take a minute for the Lodestone to show a change.",
        SharingNoticeKind.TurnedOff => "Sharing is off for this character. The server deleted what it held for it.",
        SharingNoticeKind.TakenOver => "Another AetherFrame, on another PC or after a reinstall, checked this character, so it now shares from there. The server deleted what this PC shared. To share from here again, turn sharing on again.",
        SharingNoticeKind.NoLongerBound => "The server no longer holds this character, so sharing is off for it here. This happens when the Lodestone no longer shows the character, or it was removed on request. You can turn sharing on again.",
        SharingNoticeKind.Renamed => "The server read this character's Lodestone page again, and now finds it by its current name and World.",
        SharingNoticeKind.TooMany => "That's been tried too many times for now. Please wait a while, then try again.",
        SharingNoticeKind.TryLater => "The sharing server is busy. Please try again in a few minutes.",
        SharingNoticeKind.Unreachable => "AetherFrame couldn't reach the sharing server. Check your connection, or try again later.",
        SharingNoticeKind.UpdateNeeded => "The sharing server needs a newer AetherFrame. Please update it, then try again.",
        SharingNoticeKind.KeyUnavailable => "This character's key can't be opened on this PC: it may be damaged, or it was made under another Windows account or on another PC. You can start again with a new key; a new Lodestone check moves sharing to it.",
        SharingNoticeKind.Refused => "The sharing server refused that, or answered in a way AetherFrame doesn't understand. Please try again later.",
        SharingNoticeKind.SaveFailed => "AetherFrame couldn't save your sharing settings on this PC. Nothing else was changed.",
        SharingNoticeKind.Failed => "Something went wrong. Nothing was shared. Please try again.",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Whether a notice reports a problem, rather than progress.</summary>
    internal static bool IsProblem(SharingNoticeKind kind) =>
        kind is not (SharingNoticeKind.CodeReady or SharingNoticeKind.CheckPassed or SharingNoticeKind.TurnedOff or SharingNoticeKind.Renamed);
}
