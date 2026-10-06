namespace AetherFrame.UI.Tutorial;

/// <summary>What to do about the tutorial when the plugin has loaded.</summary>
internal enum FirstRunDecision
{
    /// <summary>A new player, whom guided creation may welcome (named when the tutorial was offered instead).</summary>
    OfferTutorial,

    /// <summary>An established player: never offer unasked; the tutorial waits under Help.</summary>
    ExistingInstall,

    /// <summary>The player already answered (or the offer was shown enough): nothing to ask.</summary>
    AlreadyDecided,

    /// <summary>Can't tell yet (the Library didn't load): ask nothing, store nothing.</summary>
    Undetermined,
}

/// <summary>
/// Tells a genuinely new player from one upgrading from v0.1.6 or earlier, without touching a
/// Plate. An established install shows itself in one of two ways: a configuration file (every
/// v0.1.6 install writes one on its first load, whatever the version before) or saved Plates or
/// Templates in the Library, read only after the Library has loaded. Only an install with neither
/// is new. The kind is decided once and stored, after which the stored kind is authoritative — so
/// the configuration this very build writes on its first load can never turn a new player into an
/// "existing" one on their second launch: a first load that found no configuration marks the
/// stored kind <see cref="TutorialInstallKind.PendingDecision"/> before anything is saved, and a
/// later launch decides from the Library as if no configuration had been found.
/// </summary>
internal static class FirstRunDetector
{
    /// <summary>A first-run offer stops after this many unanswered showings, as if it were put off (the welcome uses it too).</summary>
    internal const int MaxOffers = 3;

    /// <param name="preferences">The stored tutorial state.</param>
    /// <param name="configurationFound">Whether a configuration file was loaded at startup (captured before this build saves one).</param>
    /// <param name="configurationUnreadable">Whether a configuration file existed but couldn't be read (only an established install has one; this outweighs every stored kind but a decided one).</param>
    /// <param name="libraryLoaded">Whether both the Plate and the Template Library loaded (the counts mean nothing otherwise).</param>
    /// <param name="plateCount">Saved Plates in the Library, Trash excluded.</param>
    /// <param name="userTemplateCount">Saved (not built-in) Templates.</param>
    internal static FirstRunDecision Decide(
        TutorialPreferences preferences, bool configurationFound, bool configurationUnreadable, bool libraryLoaded, int plateCount, int userTemplateCount)
    {
        if (preferences.Status != TutorialStatus.Undecided)
        {
            return FirstRunDecision.AlreadyDecided;
        }

        switch (preferences.Install)
        {
            case TutorialInstallKind.ExistingInstall:
                return FirstRunDecision.AlreadyDecided;
            case TutorialInstallKind.NewInstall:
                return preferences.OfferCount >= MaxOffers ? FirstRunDecision.AlreadyDecided : FirstRunDecision.OfferTutorial;
            case TutorialInstallKind.PendingDecision:
                // The configuration on disk is the one this build wrote before the Library was
                // read: its presence says nothing about the player, so only the Library decides.
                // A file that couldn't be read is different: this build never stores "pending"
                // for one, and if it somehow met one it would still be an established install's.
                configurationFound = false;
                break;
        }

        if (configurationFound || configurationUnreadable)
        {
            return FirstRunDecision.ExistingInstall;
        }

        if (!libraryLoaded)
        {
            return FirstRunDecision.Undetermined;
        }

        return plateCount > 0 || userTemplateCount > 0 ? FirstRunDecision.ExistingInstall : FirstRunDecision.OfferTutorial;
    }
}
