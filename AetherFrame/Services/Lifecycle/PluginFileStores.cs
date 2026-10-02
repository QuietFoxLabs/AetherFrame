using AetherFrame.Persistence;
using AetherFrame.Services.Diagnostics;

namespace AetherFrame.Services.Lifecycle;

/// <summary>
/// The two ways the plugin writes its structured files, both over Dalamud's reliable storage in game
/// (rule 3) and both keeping a Plate saveable when that storage leaves a temporary file stuck open
/// (<see cref="StuckTempFallbackFileStore"/>). <see cref="Guarded"/> is every Library operation's:
/// it stops between two files once unloading abandons the operation (see
/// <see cref="ShutdownGuardedFileStore"/>). <see cref="Unguarded"/> is the same chain without that
/// guard, for the one write unloading makes itself (the unsaved changes an editor had, see
/// <c>UnsavedChangesKeeper</c>): no operation owns it, so nothing may refuse it for having been
/// abandoned, and it runs before Dalamud disposes the storage.
/// </summary>
internal sealed class PluginFileStores
{
    /// <param name="reliable">Dalamud's reliable storage in game (<c>ReliablePlateFileStore</c>); plain files in tests.</param>
    internal PluginFileStores(IPlateFileStore reliable, OwnedOperations operations, IAetherFrameLog log)
    {
        Unguarded = new StuckTempFallbackFileStore(reliable, log);
        Guarded = new ShutdownGuardedFileStore(Unguarded, operations);
    }

    /// <summary>Every owned operation's store: an abandoned one stops before its next file step.</summary>
    internal IPlateFileStore Guarded { get; }

    /// <summary>Only for the write unloading makes itself, never for an owned operation.</summary>
    internal IPlateFileStore Unguarded { get; }
}
