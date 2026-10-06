using System;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.Services.Diagnostics;
using AetherFrame.Services.Lifecycle;
using AetherFrame.Services.Plates;

namespace AetherFrame.UI.Editor;

/// <summary>
/// Keeps an editor's unsaved changes when AetherFrame unloads (a test build's reload, an update, a
/// disable, the game closing), so the next load can offer them back (see
/// <c>KeptChangesOffer</c>): as a draft beside the Library, never in the Plate itself. A crash is
/// covered by the recovery checkpoints written while editing (<see cref="ContinuousRecovery"/>); this
/// draft is the best-effort last word of that same editing, named for it, so the next load offers both
/// as one choice.
///
/// <para>Two steps, in unloading's order. <see cref="Capture"/> runs once nothing draws any more and
/// before the windows go, while the editor that showed the Plate is still known; it reads the open
/// document through <see cref="EditorSession.CaptureUnsavedChanges"/>, so it needs neither ImGui nor
/// the framework thread. <see cref="WriteAsync"/> then writes it, before unloading waits for running
/// operations: through the reliable storage's unguarded chain (<see cref="PluginFileStores.Unguarded"/>),
/// never as a Library operation and never dispatched to the framework thread, both of which unloading
/// or a closing game can refuse or never run. It is waited for at most a few seconds, and never
/// throws: a failure is logged and unloading goes on.</para>
/// </summary>
internal sealed class UnsavedChangesKeeper
{
    /// <summary>How long unloading waits for the draft to be written.</summary>
    internal static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    private readonly EditorSession session;
    private readonly Func<EditorSurfaceKind?> activeSurface;
    private readonly DraftStore writer;
    private readonly string build;
    private readonly IAetherFrameLog log;

    private readonly Func<ProfileDocument, (Guid SessionId, Guid EditId)?>? editingOf;

    private int captured;
    private PlateDraft? draft;

    /// <param name="activeSurface">Which editor shows the open Plate, if any.</param>
    /// <param name="build">The running build, recorded in the draft for diagnosis.</param>
    /// <param name="editingOf">The recovery editing a document belongs to (see <see cref="ContinuousRecovery.EditingOf"/>), or null.</param>
    internal UnsavedChangesKeeper(
        EditorSession session, Func<EditorSurfaceKind?> activeSurface, PlateStoragePaths paths, PluginFileStores stores, string build, IAetherFrameLog log, Func<DateTime>? utcNow = null,
        Func<ProfileDocument, (Guid SessionId, Guid EditId)?>? editingOf = null)
    {
        this.editingOf = editingOf;
        this.session = session;
        this.activeSurface = activeSurface;
        writer = new DraftStore(paths, stores.Unguarded, log, utcNow);
        this.build = build;
        this.log = log;
    }

    /// <summary>
    /// Takes a copy of the open Plate's unsaved changes, if it has any, for <see cref="WriteAsync"/>.
    /// Once per unload (a second call does nothing), on whichever thread unloading runs it; never throws.
    /// </summary>
    internal void Capture()
    {
        if (Interlocked.Exchange(ref captured, 1) != 0)
        {
            return;
        }

        try
        {
            if (session.CaptureUnsavedChanges() is not { } copy)
            {
                return;
            }

            var editor = activeSurface() switch
            {
                EditorSurfaceKind.Basic => DraftEditor.Basic,
                EditorSurfaceKind.Advanced => DraftEditor.Advanced,
                _ => DraftEditor.None,
            };
            var kept = writer.Create(copy, editor, build);
            if (editingOf?.Invoke(copy.Source) is { } editing)
            {
                kept.SessionId = editing.SessionId;
                kept.EditId = editing.EditId;
            }

            Volatile.Write(ref draft, kept);
        }
        catch (Exception ex)
        {
            log.Error(ex, "AetherFrame couldn't read the open Plate's unsaved changes while unloading, so they weren't kept.");
        }
    }

    /// <summary>
    /// Writes what <see cref="Capture"/> took, waiting for it at most <paramref name="timeout"/>.
    /// Never throws: a refusal or failure is logged, as is a write still running at the timeout
    /// (whose eventual outcome is logged too).
    /// </summary>
    internal async Task WriteAsync(TimeSpan timeout)
    {
        if (Interlocked.Exchange(ref draft, null) is not { } toWrite)
        {
            return;
        }

        try
        {
            var write = Task.Run(() => writer.WriteAsync(toWrite));
            using var timer = new CancellationTokenSource();
            var finished = await Task.WhenAny(write, Task.Delay(timeout, timer.Token)).ConfigureAwait(false);
            timer.Cancel();
            if (finished != write)
            {
                log.Warning($"AetherFrame stopped waiting after {timeout.TotalSeconds:0.#}s to keep the unsaved changes of Plate {toWrite.PlateId} while unloading.");
                _ = write.ContinueWith(task => Observe(task, toWrite.PlateId), TaskScheduler.Default);
                return;
            }

            await write.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Error(ex, $"AetherFrame couldn't keep the unsaved changes of Plate {toWrite.PlateId} while unloading.");
        }
    }

    private void Observe(Task write, Guid plateId)
    {
        if (write.IsFaulted)
        {
            log.Error(write.Exception?.GetBaseException(), $"AetherFrame couldn't keep the unsaved changes of Plate {plateId} while unloading.");
        }
    }
}
