using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.Services.Diagnostics;

namespace AetherFrame.Services.Thumbnails;

internal enum PlateThumbnailState
{
    /// <summary>No thumbnail for this version of the Plate (and none being made): show the fallback card.</summary>
    Missing,

    /// <summary>Being generated in the background: show the fallback card meanwhile.</summary>
    Generating,

    /// <summary>A thumbnail image for exactly this version of the Plate exists at <see cref="PlateThumbnail.ImagePath"/>.</summary>
    Ready,

    /// <summary>Generating or displaying it failed; not retried until the Plate changes. Show the fallback card.</summary>
    Failed,
}

internal readonly record struct PlateThumbnail(PlateThumbnailState State, string? ImagePath);

/// <summary>
/// Renders a Plate document to a PNG thumbnail file. The extension point for a future CPU
/// compositor: implementations run off the render thread, must treat the document as read-only,
/// and may throw — a failure only ever marks that thumbnail <see cref="PlateThumbnailState.Failed"/>.
/// The output path is a fresh temporary file beside the thumbnail; the service moves it into
/// place afterwards and deletes it whatever happens.
/// </summary>
internal interface IPlateThumbnailGenerator
{
    Task GenerateAsync(ProfileDocument document, string outputPngPath, CancellationToken cancellationToken);
}

/// <summary>
/// Thumbnails are derived data: cached PNGs ("thumbnails/{guid}.png") tagged with the Plate
/// version they were made from ("{guid}.key"), regenerated on demand, safe to delete at any time.
/// The Plate document is always the source of truth, and nothing here can block saving, opening,
/// editing, previewing, activating, duplicating, or deleting a Plate: every call is non-blocking
/// and every failure degrades to the fallback card.
///
/// A generation writes a uniquely named temporary file (".{guid}.png.{id}.tmp") that is removed
/// however the generation ends; leftovers from a crash are swept before the first generation (or
/// by <see cref="SweepTemporaryFiles"/>) and with <see cref="Remove"/>. Generations still running
/// are cancelled and briefly awaited by <see cref="Dispose"/>, so nothing is written after it.
///
/// With no <see cref="IPlateThumbnailGenerator"/> (the current state: AetherFrame has no offscreen
/// renderer, and renders nothing with native game or GPU render targets), thumbnails are simply
/// <see cref="PlateThumbnailState.Missing"/> unless a matching file already exists.
/// </summary>
internal sealed class PlateThumbnailService : IDisposable
{
    /// <summary>How long <see cref="Dispose"/> waits for running generations by default.</summary>
    private static readonly TimeSpan DefaultDisposeWait = TimeSpan.FromSeconds(2);

    private readonly string directory;
    private readonly IPlateThumbnailGenerator? generator;
    private readonly IAetherFrameLog log;
    private readonly TimeSpan disposeWait;
    private readonly object gate = new();
    private readonly Dictionary<Guid, Entry> entries = new();
    private readonly HashSet<Task> generations = new();
    private readonly HashSet<string> temporaryFilesInUse = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim generationSlot = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private bool swept;
    private bool disposed;

    internal PlateThumbnailService(string directory, IPlateThumbnailGenerator? generator = null, IAetherFrameLog? log = null, TimeSpan? disposeWait = null)
    {
        this.directory = directory;
        this.generator = generator;
        this.log = log ?? NullAetherFrameLog.Instance;
        this.disposeWait = disposeWait ?? DefaultDisposeWait;
    }

    /// <summary>A version key that changes whenever the Plate's saved state does.</summary>
    internal static string VersionKeyFor(int revision, DateTime modifiedUtc) => $"r{revision}-{modifiedUtc.Ticks}";

    internal string GetImagePath(Guid plateId) => Path.Combine(directory, $"{plateId}.png");

    private string GetKeyPath(Guid plateId) => Path.Combine(directory, $"{plateId}.key");

    /// <summary>
    /// The thumbnail state for this version of the Plate. Cheap to call every frame: the disk is
    /// only consulted the first time a version is seen, and generation (when available) runs in
    /// the background. <paramref name="documentProvider"/> is only called off the calling thread,
    /// during generation. After <see cref="Dispose"/> no generation is started.
    /// </summary>
    internal PlateThumbnail Get(Guid plateId, string versionKey, Func<ProfileDocument?> documentProvider)
    {
        lock (gate)
        {
            if (entries.TryGetValue(plateId, out var cached) && cached.Key == versionKey)
            {
                return cached.Thumbnail;
            }

            var thumbnail = ResolveFromDisk(plateId, versionKey);
            if (thumbnail.State == PlateThumbnailState.Missing && generator is not null && !disposed)
            {
                thumbnail = new PlateThumbnail(PlateThumbnailState.Generating, null);
                entries[plateId] = new Entry(versionKey, thumbnail);
                // Off the caller's thread entirely: the caller (ImGui Draw) never waits on it.
                // Tracked so Dispose can wait for it (bounded) before the process moves on.
                var generation = Task.Run(() => GenerateAsync(plateId, versionKey, documentProvider));
                generations.Add(generation);
                generation.ContinueWith(Untrack, TaskContinuationOptions.ExecuteSynchronously);
                return thumbnail;
            }

            entries[plateId] = new Entry(versionKey, thumbnail);
            return thumbnail;
        }
    }

    /// <summary>A Ready thumbnail's image couldn't be displayed (e.g. a corrupt file): fall back
    /// for this version rather than retrying every frame.</summary>
    internal void ReportDisplayFailure(Guid plateId, string reason)
    {
        lock (gate)
        {
            if (entries.TryGetValue(plateId, out var entry) && entry.Thumbnail.State == PlateThumbnailState.Ready)
            {
                entries[plateId] = entry with { Thumbnail = new PlateThumbnail(PlateThumbnailState.Failed, null) };
                log.Warning($"AetherFrame could not display the thumbnail for Plate {plateId}: {reason}");
            }
        }
    }

    /// <summary>Forgets what's known about a Plate's thumbnail (e.g. after it's saved).</summary>
    internal void Invalidate(Guid plateId)
    {
        lock (gate)
        {
            entries.Remove(plateId);
        }
    }

    /// <summary>Forgets and deletes a Plate's derived thumbnail files (e.g. after it's deleted),
    /// including any temporary file an interrupted generation left behind. Never throws.</summary>
    internal void Remove(Guid plateId)
    {
        Invalidate(plateId);
        TryDelete(GetImagePath(plateId));
        TryDelete(GetKeyPath(plateId));
        foreach (var leftover in ListLeftoverTemporaryFiles(plateId))
        {
            TryDelete(leftover);
        }
    }

    /// <summary>
    /// Deletes temporary files left behind by a generation that the game closing (or a crash)
    /// interrupted. Only files with this service's own naming are ever removed, and never one a
    /// running generation is still writing. Runs once before the first generation; the host may
    /// also call it at startup. Never throws.
    /// </summary>
    internal void SweepTemporaryFiles()
    {
        lock (gate)
        {
            swept = true;
        }

        foreach (var leftover in ListLeftoverTemporaryFiles(plateId: null))
        {
            TryDelete(leftover);
        }
    }

    /// <summary>Stops generations, waits (bounded) for the running ones to end, then releases
    /// the generation resources. Safe to call more than once.</summary>
    public void Dispose()
    {
        Task[] pending;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            pending = [.. generations];
        }

        shutdown.Cancel();
        try
        {
            // A generator that ignores its token can outlive this wait; it then finds the token
            // cancelled before the move and writes nothing (see GenerateAsync).
            Task.WhenAll(pending).Wait(disposeWait);
        }
        catch (AggregateException)
        {
            // GenerateAsync reports its own failures; nothing to add here.
        }

        shutdown.Dispose();
        generationSlot.Dispose();
    }

    private PlateThumbnail ResolveFromDisk(Guid plateId, string versionKey)
    {
        try
        {
            var imagePath = GetImagePath(plateId);
            var keyPath = GetKeyPath(plateId);
            if (File.Exists(imagePath) && File.Exists(keyPath) && File.ReadAllText(keyPath, Encoding.UTF8).Trim() == versionKey)
            {
                return new PlateThumbnail(PlateThumbnailState.Ready, imagePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warning($"AetherFrame could not check the thumbnail for Plate {plateId} ({ex.GetType().Name}).");
        }

        return new PlateThumbnail(PlateThumbnailState.Missing, null);
    }

    private async Task GenerateAsync(Guid plateId, string versionKey, Func<ProfileDocument?> documentProvider)
    {
        CancellationToken token;
        try
        {
            token = shutdown.Token;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        var result = new PlateThumbnail(PlateThumbnailState.Failed, null);

        try
        {
            await generationSlot.WaitAsync(token).ConfigureAwait(false);
            try
            {
                SweepTemporaryFilesOnce();

                var document = documentProvider() ?? throw new InvalidOperationException("The Plate is not available.");
                Directory.CreateDirectory(directory);

                var imagePath = GetImagePath(plateId);
                var temporaryPath = Path.Combine(directory, $".{plateId}.png.{Guid.NewGuid():N}.tmp");
                lock (gate)
                {
                    temporaryFilesInUse.Add(temporaryPath);
                }

                try
                {
                    await generator!.GenerateAsync(document, temporaryPath, token).ConfigureAwait(false);

                    // Nothing is written once Dispose has begun, even by a generator that
                    // finished regardless of its token.
                    token.ThrowIfCancellationRequested();
                    File.Move(temporaryPath, imagePath, overwrite: true);
                    SystemFileStore.WriteAtomically(GetKeyPath(plateId), Encoding.UTF8.GetBytes(versionKey));
                    result = new PlateThumbnail(PlateThumbnailState.Ready, imagePath);
                }
                finally
                {
                    TryDelete(temporaryPath);
                    lock (gate)
                    {
                        temporaryFilesInUse.Remove(temporaryPath);
                    }
                }
            }
            finally
            {
                ReleaseGenerationSlot();
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        catch (Exception ex)
        {
            log.Warning($"AetherFrame could not generate a thumbnail for Plate {plateId} ({ex.GetType().Name}).");
        }

        lock (gate)
        {
            // Only if nothing newer was asked for in the meantime.
            if (entries.TryGetValue(plateId, out var entry) && entry.Key == versionKey)
            {
                entries[plateId] = entry with { Thumbnail = result };
            }
        }
    }

    private void ReleaseGenerationSlot()
    {
        try
        {
            generationSlot.Release();
        }
        catch (ObjectDisposedException)
        {
            // Dispose stopped waiting for this generation; there is nothing left to hand over.
        }
    }

    private void SweepTemporaryFilesOnce()
    {
        lock (gate)
        {
            if (swept)
            {
                return;
            }
        }

        SweepTemporaryFiles();
    }

    private void Untrack(Task generation)
    {
        lock (gate)
        {
            generations.Remove(generation);
        }
    }

    /// <summary>This service's own temporary files (for one Plate, or all of them), except any a
    /// running generation is still writing. Empty when the directory doesn't exist or can't be listed.</summary>
    private IReadOnlyList<string> ListLeftoverTemporaryFiles(Guid? plateId)
    {
        var leftovers = new List<string>();
        try
        {
            if (!Directory.Exists(directory))
            {
                return leftovers;
            }

            foreach (var path in Directory.GetFiles(directory, "*.tmp"))
            {
                if (!TryParseTemporaryFileName(Path.GetFileName(path), out var owner) || (plateId is { } wanted && owner != wanted))
                {
                    continue;
                }

                lock (gate)
                {
                    if (temporaryFilesInUse.Contains(path))
                    {
                        continue;
                    }
                }

                leftovers.Add(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warning($"AetherFrame could not look for old temporary thumbnail files ({ex.GetType().Name}).");
        }

        return leftovers;
    }

    /// <summary>Recognises the temporary files this service (and the key writer) create:
    /// ".{guid}.png.{id}.tmp" and ".{guid}.key.{id}.tmp", plus the older "{guid}.png.tmp".</summary>
    private static bool TryParseTemporaryFileName(string fileName, out Guid plateId)
    {
        plateId = default;
        if (!fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = fileName.StartsWith('.') ? fileName[1..] : fileName;
        const int guidLength = 36;
        if (name.Length <= guidLength || !Guid.TryParseExact(name[..guidLength], "D", out plateId))
        {
            return false;
        }

        var suffix = name[guidLength..];
        return suffix.StartsWith(".png.", StringComparison.OrdinalIgnoreCase)
            || suffix.StartsWith(".key.", StringComparison.OrdinalIgnoreCase);
    }

    private void TryDelete(string path)
    {
        try
        {
            // A missing file is a no-op; a locked file or a directory in the way is reported.
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing was ever generated: the thumbnails directory itself doesn't exist yet.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warning($"AetherFrame could not remove the thumbnail file {Path.GetFileName(path)} ({ex.GetType().Name}).");
        }
    }

    private sealed record Entry(string Key, PlateThumbnail Thumbnail);
}
