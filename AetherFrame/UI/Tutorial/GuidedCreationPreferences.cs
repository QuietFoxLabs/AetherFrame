using System;

namespace AetherFrame.UI.Tutorial;

/// <summary>How the player answered the welcome's offer to create a Plate step by step.</summary>
public enum GuidedOfferAnswer
{
    /// <summary>Never answered (the welcome may still be shown, a few times at most).</summary>
    Undecided = 0,

    /// <summary>Not now: the welcome may ask again on a later load (a few showings in all), while there is still no Plate.</summary>
    Deferred,

    /// <summary>Don't show again: no welcome from now on. An empty My Plates and Help still offer the steps.</summary>
    Declined,

    /// <summary>The player started guided creation (from the welcome or anywhere else).</summary>
    Accepted,
}

/// <summary>Where a guided creation stands.</summary>
public enum GuidedRunStatus
{
    /// <summary>No guided creation was ever started.</summary>
    None = 0,

    /// <summary>Started and not saved at the Save step yet: its Plate opens on the step it reached.</summary>
    InProgress,

    /// <summary>The player chose Exit Guide: the Plate stays, edited normally; starting again makes a new Plate.</summary>
    Left,

    /// <summary>Saved at the Save step.</summary>
    Completed,
}

/// <summary>The three steps of guided creation, in order. A value this build doesn't know reads as the first step.</summary>
public enum GuidedStage
{
    ChooseLook = 0,
    MakeItYours = 1,
    Save = 2,
}

/// <summary>Which of its two views the Basic editor shows.</summary>
public enum BasicWorkspaceMode
{
    /// <summary>Never chosen: an existing player keeps the Detailed view they always had.</summary>
    Unset = 0,

    /// <summary>The everyday controls first (look, name, portrait, message), the rest folded away.</summary>
    Simple,

    /// <summary>Every control of every category in view, as before.</summary>
    Detailed,
}

/// <summary>
/// Guided creation's persisted state, stored in the plugin configuration beside the tutorial's and
/// never inside a Plate: the welcome's answer and how often it was shown, the Plate a guided
/// creation made and the step it reached (so closing, a reload or a crash resumes that Plate rather
/// than making another), and whether it was completed. Plain properties so it serializes by
/// convention. As with <see cref="TutorialPreferences"/>, a property a newer build adds inside this
/// block is dropped by this build's next save, so a newer build must tolerate its absence.
/// </summary>
public sealed class GuidedCreationPreferences
{
    public GuidedOfferAnswer Offer { get; set; }

    /// <summary>How many times the welcome was shown (it stops after a few unanswered showings).</summary>
    public int OfferCount { get; set; }

    public GuidedRunStatus Run { get; set; }

    /// <summary>The Plate the current (or last) guided creation made; null before the first one.</summary>
    public Guid? PlateId { get; set; }

    /// <summary>The step the guided creation reached.</summary>
    public GuidedStage Stage { get; set; }

    /// <summary>How many guided creations were completed (saved at the Save step).</summary>
    public int CompletedCount { get; set; }
}

/// <summary>Where guided creation's state and the Basic editor's view live (the plugin configuration), and how they're saved.</summary>
internal interface IGuidedCreationStore
{
    GuidedCreationPreferences Preferences { get; }

    BasicWorkspaceMode Workspace { get; set; }

    /// <summary>Persists both. Never throws: failing only means a choice may be asked again.</summary>
    void Save();
}
