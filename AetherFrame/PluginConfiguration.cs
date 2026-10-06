using System;
using System.Collections.Generic;
using AetherFrame.UI.Tutorial;
using Dalamud.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AetherFrame;

[Serializable]
public sealed class PluginConfiguration : IPluginConfiguration
{
    /// <summary>The version that added <see cref="BasicGuidanceHandled"/>; an older saved configuration predates it.</summary>
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>
    /// Whether the one-time "New to AetherFrame?" suggestion of the Basic Editor has been handled
    /// (see <c>BasicGuidance</c>). Once true it is never shown again.
    /// </summary>
    public bool BasicGuidanceHandled { get; set; }

    /// <summary>
    /// The interactive tutorial's own state (whether this install was new when the tutorial first
    /// ran, how the first-run offer was answered, where a started tour stopped, which version was
    /// completed). Null in a configuration written before the tutorial existed, which is not by
    /// itself a sign of a new player: see <c>FirstRunDetector</c>. Kept apart from every Plate.
    /// </summary>
    public TutorialPreferences? Tutorial { get; set; }

    /// <summary>
    /// Guided creation's own state (the welcome's answer, the Plate a guided creation made and the
    /// step it reached, whether it was completed). Null in a configuration written before guided
    /// creation existed. Kept apart from every Plate.
    /// </summary>
    public GuidedCreationPreferences? GuidedCreation { get; set; }

    /// <summary>
    /// Which view the Basic editor shows. Unset in a configuration written before the Simple view
    /// existed, which keeps the Detailed view an existing player always had; a new player starts in
    /// the Simple view. Either is the player's to switch.
    /// </summary>
    public BasicWorkspaceMode BasicWorkspace { get; set; }

    /// <summary>
    /// Whatever a newer version of AetherFrame stored here and this one doesn't know: kept as it
    /// is and written back unchanged, so going back to this version never loses a newer one's
    /// settings (the same forward compatibility every Plate and Template file has).
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JToken>? ExtensionData { get; set; }
}
