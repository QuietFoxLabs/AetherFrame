using System;

namespace AetherFrame.Services.Network.Sharing;

/// <summary>
/// The words of the sharing windows (N2-9b, and the progress window of October 2, 2026): the
/// consent a character's opting in asks for (decisions C3, C5, C7, K2 and K4 as C9 restates it),
/// the Lodestone steps (C2), each step of sharing the Active Plate, and what each outcome means.
/// None of it holds a name, a World, a code or an id: the windows draw those on their own,
/// unformatted.
/// </summary>
internal static class SharingText
{
    internal const string Intro =
        "Sharing lets players who use AetherFrame, and have turned sharing on, view each other's Active Plates, like the game's Adventure Plates. It's off until you turn it on, one character at a time.";

    internal const string ConsentTitle = "Before you turn sharing on for this character";

    /// <summary>What turning sharing on means, one statement each (C3, C5, C7, K2, and K4 as C9 restates it).</summary>
    internal static readonly string[] Consent =
    [
        "Other players who have turned sharing on can view this character's Active Plate as a picture: by right-clicking the character in game, or by searching for its name and World. They never get the Plate itself, its Template or your original images.",
        "Once sharing is on, this character's Active Plate is shared without asking: when the check passes, each time you save it, and each time you make another Plate Active. To see what a Plate would share before that, choose Check what would be shared (preview) in its menu in My Plates. My Plates marks the shared Plate, and you can pause or turn off sharing at any time.",
        "Turning sharing off deletes this character's Plate, its images and its check from the server at once. The server's backups keep copies for up to 7 days before they are deleted too.",
        "Other players can report a Plate. The server keeps a report (the character, the reason, and the reporting player's key) for up to 30 days, or until it is dealt with, even if sharing is turned off.",
        "Anyone who has turned sharing on can find out, from this character's name, that its player uses AetherFrame, and so Dalamud.",
        "The server keeps this character's Lodestone id, name and World, the day its Lodestone page was last read, a key that proves the character is yours, and your latest shared Plate. It sees the network address of anyone who shares or views, and doesn't store it. It keeps no record of who viewed whom.",
        "The key stays on this PC. If you share this character from another PC, or after reinstalling Windows, checking it again there moves sharing to it, and what this PC shared is deleted.",
        "Windows protects the key for your account. A copy of your Windows profile opens it wherever your Windows password is known, and at once if your account has no password. Any program running as you, other Dalamud plugins included, can use it. On a work or school PC, your organisation may be able to recover it.",
        "To prove the character is yours, you'll paste a short code into its Lodestone profile. While the code is there, anyone reading the profile can see that you use AetherFrame, so delete it once the check passes.",
        ReadsThroughYourConnection,
        ThirtyDays,
        OnlineCountSends,
    ];

    /// <summary>What reading the Lodestone page through the player's own connection means: in the consent, and in the one-time notice.</summary>
    internal const string ReadsThroughYourConnection =
        "The sharing server reads this character's Lodestone page through your own internet connection: when you check it, and later when you save your Active Plate, open this window or view a Plate, if the page is due to be read again. The Lodestone sees your network address, and a request that names AetherFrame and its server. AetherFrame only passes encrypted data along, and can't read or change the page.";

    /// <summary>The 30 days after which a character not read again stops showing its Plate.</summary>
    internal const string ThirtyDays =
        "If this character's Lodestone page isn't read for 30 days, other players stop seeing its Plate until you next use sharing with it. Nothing is deleted.";

    /// <summary>What the online count sends and keeps ("The online count"): in the consent, and in the one-time notice.</summary>
    internal const string OnlineCountSends =
        "While this character is logged in with sharing on, AetherFrame tells the sharing server about once a minute that it is online, so My Plates can show how many sharing characters are online. The server holds this in memory only, for 3 minutes after the last signal, counts each character once, and only ever gives out the total, as \"fewer than 5\" while it is under 5. That total still moves by one when a sharing character logs in or out once 5 or more are online, and below 5 for a player who adds characters of their own, so another sharing player who watches it closely can sometimes tell when this character logs in or out. The server keeps no record of who was online: its log notes when each signal came, never whose, for 14 days, and its rate limits remember, in memory for up to an hour, when this character started being counted. Logging out, pausing or turning off sharing, or closing the game stops it, and the server stops counting the character at once, or within about 3 minutes if the game crashes. Characters that don't share send nothing.";

    internal const string OnlineNoticeTitle = "My Plates now shows how many are online";

    /// <summary>The one-time notice for players who shared before the online count: nothing of it is sent until they dismiss it.</summary>
    internal static readonly string[] OnlineNotice =
    [
        OnlineCountSends,
        "Each signal shows the server your network address, which it never writes down or logs, and holds in memory only for its rate limits. Nothing is sent until you choose Got it.",
    ];

    internal const string ConnectionNoticeTitle = "Checks now use your own connection";

    /// <summary>The one-time notice for players who shared before checks went through their own connection.</summary>
    internal static readonly string[] ConnectionNotice =
    [
        ReadsThroughYourConnection,
        ThirtyDays,
        "Some VPNs, proxies and hosting services are turned away by the Lodestone. If that happens, AetherFrame tells you, and you can try again from another connection.",
    ];

    internal const string ReadAgainLine =
        "Its Lodestone page is read again through your connection the first time you use sharing after AetherFrame starts, and when its name or World changes. If it isn't read for 30 days, other players stop seeing its Plate until you next use sharing with it.";

    internal const string Agree = "I understand, and I want to share this character's Active Plate.";

    internal const string TurnOn = "Turn on sharing";

    internal const string NewKeyTitle = "Start again with a new key";

    internal const string NewKeyBound =
        "The server still shares this character under the key that can't be opened here. Checking the character with a new key moves sharing to it; until then, nothing changes.";

    internal const string CodeStepCopy = "1. Copy this code:";

    internal const string CodeWarning = "Only ever paste a code your own AetherFrame shows you here.";

    internal const string CodeStepPaste =
        "2. Sign in to the Lodestone, open your character's Character Profile in its settings, paste the code anywhere in it, and save.";

    internal const string CodeStepAddress = "3. Paste the address of your character's Lodestone page here, then press Check:";

    internal const string AddressHint = "https://na.finalfantasyxiv.com/lodestone/character/12345678/";

    internal const string AddressInvalid = "That isn't a Lodestone character page's address. Open your character's page on the Lodestone and copy the address from your browser.";

    internal const string CodeLife =
        "The code works once. If a check doesn't pass, you can fix the profile and check again. If AetherFrame reloads before the check passes, get a new code and paste that one instead.";

    internal const string CodeExpired = "This code has run out. Get a new one.";

    internal const string NoCodeYet = "Get a code to start the Lodestone check.";

    internal const string NoNameYet = "AetherFrame can't read your character's name and World right now. Try again in a moment.";

    internal const string SharedLine = "Sharing is on for this character, as it appears on the Lodestone:";

    internal const string SavingShares =
        "Saving this character's Active Plate shares the new version, and making another Plate Active shares that one. My Plates marks the shared Plate.";

    internal const string PausedLine = "Sharing is paused for this character: the server holds no Plate for it, and keeps its check.";

    internal const string Building = "Preparing your Active Plate to share...";

    internal const string CantShare = "Your Active Plate can't be shared as it is. The version shared before stays up. Change what is listed here and save it again:";

    internal const string Sending = "Sending your Active Plate...";

    internal const string ProgressTitle = "Sharing your Active Plate";

    internal const string PreparingImages = "Preparing its images...";

    internal const string Signing = "Signing it on this PC...";

    internal const string SendingTakesTime = "This can take a few minutes when the sharing server is busy.";

    internal const string WaitingTurn = "Ready, and waiting for its turn...";

    internal const string WaitingForOther = "Ready, and waiting while another of your characters' Plates is sent. The Sharing window can stop that.";

    internal const string OtherSending = "Another of your characters' Active Plate is being sent:";

    internal const string OtherSendingWaits = "Until it is sent or stopped, the buttons below wait for it.";

    internal const string NotSharedYet = "Your Active Plate isn't shared yet.";

    internal const string TurnOffConfirm =
        "Turn off sharing for this character? The server deletes its Plate, its images and its check at once. To share again, you'll need a new Lodestone check.";

    internal const string TurnOffAllConfirm =
        "Turn off sharing for every character on this PC? The server deletes what it holds for each of them. To share again, each needs a new Lodestone check.";

    internal const string NoCharacter = "Log in to a character to turn sharing on or off for it.";

    internal const string Unreadable =
        "AetherFrame couldn't read the file that records which characters you share from this PC (sharing.afsh, in the folder below). It may be damaged, or saved by a newer AetherFrame. " +
        "Nothing more is sent from here until it can be read, and nothing was changed or deleted. Characters you already share stay shared on the server. " +
        "To fix it, update AetherFrame, or move the file aside and turn sharing on again for each character, which moves sharing to a new check.";

    internal const string PersonasUnavailable = "Sharing needs this PC's keys, which aren't available right now:";

    internal const string Starting = "Getting ready...";

    internal const string Busy = "Working...";

    /// <summary>How long sharing has been working, in words: minutes and seconds.</summary>
    internal static string Elapsed(TimeSpan elapsed)
    {
        var seconds = Math.Max(0, (long)elapsed.TotalSeconds);
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Still working, {seconds / 60}:{seconds % 60:00} so far.");
    }

    /// <summary>How long a code has left, in words.</summary>
    internal static string CodeLeft(TimeSpan left) =>
        left <= TimeSpan.Zero ? CodeExpired
        : left.TotalMinutes < 1.5 ? "The code runs out in about a minute."
        : "The code runs out in about " + ((int)Math.Round(left.TotalMinutes)).ToString(System.Globalization.CultureInfo.InvariantCulture) + " minutes.";

    /// <summary>What a notice means, in words, with the reason the server or the commit gave, when there is one.</summary>
    internal static string Notice(SharingNotice notice) => notice.Kind switch
    {
        SharingNoticeKind.PublishRefused => "The sharing server refused this Plate: " + RefusalReason(notice.Detail) + " The version shared before stays up.",
        SharingNoticeKind.PublishNotStored => "Your Plate couldn't be prepared for sharing on this PC, so nothing was sent. " + NotStoredReason(notice.Detail),
        _ => Notice(notice.Kind),
    };

    /// <summary>A refused publish's reason code (ServerApi-v1.md, section 4), in words.</summary>
    internal static string RefusalReason(string? reason) => reason switch
    {
        "clock-ahead" => "your PC's clock is ahead of the server's. Set your clock to the right time, then save the Plate again.",
        "image-refused" => "one of its images couldn't be processed. Try another image, then save the Plate again.",
        "not-bound" or "wrong-profile" => "the server doesn't have this character as shared from this PC any more. Open the Sharing window to check it again.",
        "revision-conflict" => "it clashed with a version sent before. Save the Plate again.",
        _ => "it isn't a Plate the server accepts. Save it again, and if it keeps happening, change what it holds.",
    };

    private static string NotStoredReason(string? result) => result switch
    {
        "OutboxFull" => "Too much is waiting to be sent from this PC; try again once the server is reachable.",
        "NotAsShown" or "CandidateUsed" => "Save the Plate again to try again.",
        _ => "Please try again.",
    };

    /// <summary>What a notice means, in words.</summary>
    internal static string Notice(SharingNoticeKind kind) => kind switch
    {
        SharingNoticeKind.CodeReady => "Here's your code. Follow the steps below.",
        SharingNoticeKind.CheckPassed => "The check passed: this character is yours on the server. You can now delete the code from your Lodestone profile.",
        SharingNoticeKind.CheckFailed => "The check didn't pass. Make sure you're logged in as the character whose page you pasted, and that the code is saved in its Character Profile, then check again. It can take a minute for the Lodestone to show a change.",
        SharingNoticeKind.TurnedOff => "Sharing is off for this character. The server deleted what it held for it.",
        SharingNoticeKind.TurnedOffAll => "Sharing is off for every character on this PC. The server deleted what it held for them.",
        SharingNoticeKind.TurnOffIncomplete => "Sharing couldn't be turned off for every character: some are still shared. Log in to each of them and turn sharing off in this window, where you'll see why if it still can't.",
        SharingNoticeKind.NewKeyDropped => "The new key was dropped. The server still shares this character under the key that can't be opened here.",
        SharingNoticeKind.TakenOver => "Another AetherFrame, on another PC or after a reinstall, checked this character, so it now shares from there. The server deleted what this PC shared. To share from here again, turn sharing on again.",
        SharingNoticeKind.NoLongerBound => "The server no longer shares this character: its Lodestone page no longer shows it, or it was taken out of the test or removed on request. Sharing is off for it here, and anything the server still held for it was deleted. You can turn sharing on again.",
        SharingNoticeKind.Renamed => "The server read this character's Lodestone page again, and now finds it by its current name and World.",
        SharingNoticeKind.TooMany => "That's been tried too many times for now. Please wait a while, then try again.",
        SharingNoticeKind.TryLater => "The sharing server is busy. Please try again in a few minutes.",
        SharingNoticeKind.Unreachable => "AetherFrame couldn't reach the sharing server. Check your connection, or try again later.",
        SharingNoticeKind.UpdateNeeded => "The sharing server needs a newer AetherFrame. Please update it, then try again. You can still turn sharing off.",
        SharingNoticeKind.KeyUnavailable => "This character's key can't be opened on this PC: it may be damaged, or it was made under another Windows account or on another PC. You can start again with a new key; a new Lodestone check moves sharing to it.",
        SharingNoticeKind.Refused => "The sharing server refused that, or answered in a way AetherFrame doesn't understand. Please try again later.",
        SharingNoticeKind.SaveFailed => "AetherFrame couldn't save your sharing settings on this PC. Nothing else was changed.",
        SharingNoticeKind.Failed => "Something went wrong. Nothing was shared. Please try again.",
        SharingNoticeKind.Published => "Your Active Plate is shared: other players who share can view it now.",
        SharingNoticeKind.PublishWaiting => "The sharing server couldn't take your Plate just now. It waits on this PC, and is sent when you try again or save it again.",
        SharingNoticeKind.PublishRefused => "The sharing server refused this Plate. The version shared before stays up.",
        SharingNoticeKind.PublishStale => "A Plate waiting to be sent was signed more than a day ago, so it wasn't sent. Save the Plate again to share it.",
        SharingNoticeKind.PublishNotStored => "Your Plate couldn't be prepared for sharing on this PC, so nothing was sent. Please try again.",
        SharingNoticeKind.Paused => "Sharing is paused: the server deleted this character's Plate, and keeps its check. Resume to share again.",
        SharingNoticeKind.Resumed => "Sharing is on again. Your Active Plate is being shared.",
        SharingNoticeKind.PublishUnrecorded => "Your Active Plate is shared, but this PC couldn't record it. It may be sent once more, which changes nothing.",
        SharingNoticeKind.PublishStopped => "Sending was stopped. If the server had already received your Plate it may be shared; otherwise it waits on this PC, and is sent when you try again or save it again.",
        SharingNoticeKind.LodestoneRefused => "The Lodestone turned your internet connection away, so your character's page couldn't be read. This can happen through some VPNs, proxies and hosting services. Try again from another connection.",
        SharingNoticeKind.PublishWithdrawn => "Sending stopped, since that Plate is no longer your Active Plate. The version shared before stays up, unless the server had already received this one.",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Whether a notice reports a problem, rather than progress.</summary>
    internal static bool IsProblem(SharingNoticeKind kind) =>
        kind is not (SharingNoticeKind.CodeReady or SharingNoticeKind.CheckPassed or SharingNoticeKind.TurnedOff or SharingNoticeKind.TurnedOffAll or SharingNoticeKind.NewKeyDropped or SharingNoticeKind.Renamed
            or SharingNoticeKind.Published or SharingNoticeKind.Paused or SharingNoticeKind.Resumed);
}
