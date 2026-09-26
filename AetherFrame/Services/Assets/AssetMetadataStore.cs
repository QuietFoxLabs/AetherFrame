using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AetherFrame.Domain.Assets;
using AetherFrame.Persistence;
using AetherFrame.Persistence.Schema;
using AetherFrame.Services.Diagnostics;

namespace AetherFrame.Services.Assets;

/// <summary>
/// Asset metadata sidecars ("asset-metadata/{guid:N}.json"), in their own directory so they can
/// never be mistaken for an asset file. Metadata is derived data: it's written at import, created
/// lazily for older assets, and an asset works exactly the same without it. Plain file IO — like
/// the assets themselves, it isn't irreplaceable document data.
/// </summary>
internal sealed class AssetMetadataStore
{
    private readonly string directory;
    private readonly IAetherFrameLog log;
    private readonly Func<DateTime> utcNow;

    private readonly object gate = new();

    /// <summary>Assets whose newer-version sidecar was already reported, so the log says it once.</summary>
    private readonly HashSet<Guid> newerVersionReported = new();

    internal AssetMetadataStore(string directory, IAetherFrameLog? log = null, Func<DateTime>? utcNow = null)
    {
        this.directory = directory;
        this.log = log ?? NullAetherFrameLog.Instance;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    internal string GetPath(Guid assetId) => Path.Combine(directory, assetId.ToString("N") + ".json");

    /// <summary>The stored metadata, or null when missing, damaged, or from a newer version.</summary>
    internal AssetMetadata? TryLoad(Guid assetId) => Usable(TryRead(assetId), assetId);

    internal void Save(AssetMetadata metadata)
    {
        metadata.Version = AssetMetadata.CurrentVersion;
        SystemFileStore.WriteAtomically(GetPath(metadata.AssetId), Encoding.UTF8.GetBytes(VersionedJson.Serialize(metadata)));
    }

    /// <summary>
    /// Stored metadata, or — for an asset imported before metadata existed, or whose metadata was
    /// lost or damaged — metadata computed from the asset file itself and then stored. A sidecar
    /// written by a newer version of AetherFrame is never written over: the computed metadata is
    /// returned for display but the file stays exactly as it is, like every other newer-version
    /// file. Reads and hashes the whole file, so never call this from ImGui Draw. Null when the
    /// asset file is unreadable.
    /// </summary>
    internal AssetMetadata? GetOrCreate(Guid assetId, string assetPath)
    {
        var stored = TryRead(assetId);
        if (Usable(stored, assetId) is { } existing)
        {
            return existing;
        }

        var inspection = ImageSafety.Inspect(assetPath);
        if (inspection is null)
        {
            return null;
        }

        var metadata = new AssetMetadata
        {
            AssetId = assetId,

            // The original name is unknown for an older asset; the managed file name is all there is.
            OriginalFileName = Path.GetFileName(assetPath),
            MediaType = inspection.MediaType,
            ByteLength = inspection.ByteLength,
            PixelWidth = inspection.Width,
            PixelHeight = inspection.Height,
            FrameCount = Math.Max(1, inspection.FrameCount),
            Sha256 = ComputeSha256(assetPath),
            CreatedAtUtc = utcNow(),
        };

        if (stored is { IsNewerVersion: true })
        {
            bool firstTime;
            lock (gate)
            {
                firstTime = newerVersionReported.Add(assetId);
            }

            if (firstTime)
            {
                log.Warning($"AetherFrame left the metadata for asset {assetId} untouched: it was written by a newer version of AetherFrame.");
            }

            return metadata;
        }

        try
        {
            Save(metadata);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warning($"AetherFrame could not store metadata for asset {assetId} ({ex.GetType().Name}).");
        }

        return metadata;
    }

    internal void Delete(Guid assetId)
    {
        var path = GetPath(assetId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The sidecar as read, or null when it is missing or couldn't be read at all. The result
    /// tells a usable file, a damaged one and a newer-version one apart; content that isn't valid
    /// JSON at all counts as damaged rather than escaping (the same set the versioned reader's
    /// own deserialization catches).
    /// </summary>
    private VersionedReadResult<AssetMetadata>? TryRead(Guid assetId)
    {
        var path = GetPath(assetId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return VersionedJson.Parse<AssetMetadata>(File.ReadAllText(path, Encoding.UTF8), PersistenceSchemas.AssetMetadata);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or InvalidOperationException or FormatException or ArgumentException)
        {
            log.Warning($"AetherFrame could not read metadata for asset {assetId} ({ex.GetType().Name}).");
            return null;
        }
    }

    /// <summary>The metadata a read result holds, if it is usable and really describes <paramref name="assetId"/>.</summary>
    private static AssetMetadata? Usable(VersionedReadResult<AssetMetadata>? result, Guid assetId) =>
        result is { IsUsable: true } && result.Value!.AssetId == assetId ? result.Value : null;

    /// <summary>Lowercase hex SHA-256 of a file's bytes, streamed (never loaded whole).</summary>
    internal static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
