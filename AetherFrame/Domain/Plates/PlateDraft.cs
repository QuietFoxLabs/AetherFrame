using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using AetherFrame.Domain.Profiles;

namespace AetherFrame.Domain.Plates;

/// <summary>Which editor showed the open Plate when AetherFrame unloaded (see <see cref="PlateDraft"/>).</summary>
public enum DraftEditor
{
    /// <summary>Neither editor was open, or a newer build recorded one this build doesn't know.</summary>
    None = 0,

    Basic = 1,

    Advanced = 2,
}

/// <summary>
/// An editor's unsaved changes, kept when AetherFrame unloaded with a Plate open and not saved: a
/// test build's reload, an update, a disable or the game closing (never a crash: nothing is written
/// while editing). It lives beside the Library, in its own folder, and never in the Plate itself;
/// the next load offers it back (see <c>DraftStore</c> and <c>KeptChangesOffer</c>). Write-once: each
/// unload writes a new file, and an answered one moves intact to the trash.
///
/// <para>Two independently versioned layers, like a Template: this envelope (see
/// <c>PersistenceSchemas.Draft</c>) and the embedded <see cref="Document"/>, which keeps
/// <c>PersistenceSchemas.ProfileDocument</c>'s own schema. Only the document is kept, never the
/// editor's undo history, selection or zoom, and no character's Content ID.</para>
/// </summary>
public sealed class PlateDraft
{
    /// <summary>This envelope's own schema version, independent of <see cref="Document"/>'s.</summary>
    public const int CurrentSchemaVersion = 1;

    private string plateName = string.Empty;
    private string build = string.Empty;

    public int Version { get; set; } = CurrentSchemaVersion;

    /// <summary>This draft's own identity; its file name carries it too.</summary>
    public Guid DraftId { get; set; }

    /// <summary>The Plate the changes were made to.</summary>
    public Guid PlateId { get; set; }

    /// <summary>The Plate's name when AetherFrame unloaded, for display only: restoring keeps the Library's.</summary>
    public string PlateName
    {
        get => plateName;
        set => plateName = value ?? string.Empty;
    }

    public DateTime WrittenAtUtc { get; set; }

    public DraftEditor Editor { get; set; }

    /// <summary>The AetherFrame build that wrote it, for diagnosis only.</summary>
    public string Build
    {
        get => build;
        set => build = value ?? string.Empty;
    }

    /// <summary>
    /// The saved version the changes started from: the open document's revision and modified time.
    /// Restoring over the Plate is offered only while its saved version is still that one.
    /// </summary>
    public int BaseRevision { get; set; }

    public DateTime BaseUpdatedAtUtc { get; set; }

    /// <summary>The whole open document, unknown data included, as a save would have written it.</summary>
    public ProfileDocument Document { get; set; } = null!;

    /// <summary>Top-level properties this build doesn't know, carried through a read.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
