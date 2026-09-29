namespace AetherFrame.UI.Tutorial;

/// <summary>Where the player stands with the tutorial.</summary>
public enum TutorialStatus
{
    /// <summary>Never asked, or asked and not answered.</summary>
    Undecided = 0,

    /// <summary>Maybe Later: not now; a quiet reminder in My Plates until it's taken or dismissed.</summary>
    Deferred,

    /// <summary>Do Not Show Again.</summary>
    Declined,

    /// <summary>Started and not finished (resumable from where it stopped).</summary>
    InProgress,

    Completed,

    /// <summary>Left before the end.</summary>
    Skipped,
}

/// <summary>What kind of installation the tutorial found when it first ran (decided once, then stored).</summary>
public enum TutorialInstallKind
{
    Unknown = 0,

    /// <summary>No configuration and no Plates: a player new to AetherFrame.</summary>
    NewInstall,

    /// <summary>A configuration or Plates from an earlier version: never treated as new.</summary>
    ExistingInstall,

    /// <summary>
    /// No configuration existed when this build first loaded, and the Library hadn't been read yet
    /// when the configuration was first saved (the Basic-first guidance saves it at once). Decided
    /// from the Library on a later launch, exactly as if no configuration had been found; never
    /// mistaken for an established install.
    /// </summary>
    PendingDecision,
}

/// <summary>
/// The tutorial's own persisted state, stored beside (never inside) any Plate: which kind of
/// install this is, whether and how the player answered the first-run offer, where a started
/// tutorial stopped, and which version of the script was completed. Plain properties so it
/// serializes by convention. The configuration that holds it keeps unknown top-level properties
/// of a newer build, but a property a newer build adds inside this block is dropped by this
/// build's next save, so a newer build must tolerate its absence.
/// </summary>
public sealed class TutorialPreferences
{
    public TutorialInstallKind Install { get; set; }

    public TutorialStatus Status { get; set; }

    /// <summary>How many times the first-run offer was shown (it stops after a few unanswered showings).</summary>
    public int OfferCount { get; set; }

    /// <summary>The script version last started (progress below refers to it).</summary>
    public int Version { get; set; }

    /// <summary>The script version last completed, 0 for never.</summary>
    public int CompletedVersion { get; set; }

    /// <summary>Where an in-progress tutorial stopped.</summary>
    public int LastChapter { get; set; }

    public int LastStep { get; set; }

    /// <summary>The quiet "take the tour" reminder in My Plates was dismissed.</summary>
    public bool ReminderDismissed { get; set; }

    internal TutorialPreferences Clone() => (TutorialPreferences)MemberwiseClone();
}

/// <summary>Where the preferences live (the plugin configuration) and how they're saved.</summary>
internal interface ITutorialPreferencesStore
{
    TutorialPreferences Preferences { get; }

    /// <summary>Persists the preferences. Never throws: failing only means the answer may be asked again.</summary>
    void Save();
}
