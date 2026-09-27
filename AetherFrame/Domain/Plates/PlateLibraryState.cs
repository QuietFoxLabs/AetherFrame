using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AetherFrame.Domain.Plates;

/// <summary>
/// Library organization only: the user's manual Plate order. Deliberately tiny, and never the
/// source of truth for which Plates exist — that is always the saved Plate documents themselves,
/// so a lost or damaged index is rebuilt from them (see <c>PlateLibraryService</c>). Unknown or
/// missing ids in <see cref="OrderedPlateIds"/> are tolerated and simply skipped when displayed.
/// </summary>
public sealed class PlateLibraryState
{
    public const int CurrentVersion = 1;

    private List<Guid> orderedPlateIds = new();

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Manual order, first shown first. Never null: an explicit JSON null (which no build
    /// writes) reads as no order, which is rebuilt like a missing index.</summary>
    public List<Guid> OrderedPlateIds
    {
        get => orderedPlateIds;
        set => orderedPlateIds = value ?? new List<Guid>();
    }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
