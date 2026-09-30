using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Personas;
using AetherFrame.Personas.Storage;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Remote;
using AetherFrame.Services.Network.Publishing;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// A persona manager over memory, whose personas hold throwaway keys generated for the test and
/// kept only in memory, and a scratch persona folder for the publication files, deleted afterwards.
/// </summary>
internal sealed class PublicationFixture : IDisposable
{
    internal PublicationFixture(bool acknowledged = true)
    {
        Root = Path.Combine(Path.GetTempPath(), "aetherframe-publication-" + Guid.NewGuid().ToString("N"));
        Files = new PublicationFiles(Root);
        Blobs = new MemoryKeyBlobs();
        Personas = PersonaManager.Load(new ProtectedPersonaKeyStore(Blobs, new MaskingProtector()), new NoBackups(), new MemoryRegistry());
        Persona = Personas.Create("Tester");
        Personas.Select(Persona.Slot);
        if (acknowledged)
        {
            Persona = Personas.Acknowledge(Persona.Slot);
        }
    }

    /// <summary>The scratch persona folder.</summary>
    internal string Root { get; }

    internal PublicationFiles Files { get; }

    internal MemoryKeyBlobs Blobs { get; }

    internal PersonaManager Personas { get; }

    /// <summary>The persona the consent screen names: created, selected and, unless told otherwise, acknowledged.</summary>
    internal PersonaRecord Persona { get; }

    /// <summary>The UTC clock the commit reads.</summary>
    internal DateTimeOffset Now { get; set; } = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);

    /// <summary>Commits <paramref name="candidate"/> as the persona shown (<see cref="Persona"/> unless given).</summary>
    internal PublishOutcome Commit(SnapshotCandidate candidate, PersonaRecord? shown = null)
    {
        var persona = shown ?? Persona;
        return PublicationCommit.Commit(Personas, Files, new PublishConsent(candidate, persona.Slot, persona.PublicKey), () => Now);
    }

    /// <summary>The index as saved, decoded.</summary>
    internal PublicationIndex SavedIndex() =>
        Files.ReadIndex(Persona.Slot) is { } bytes ? PublicationIndexCodec.Decode(bytes, Persona.Slot) : PublicationIndex.Empty;

    /// <summary>Saves <paramref name="index"/> as the persona's.</summary>
    internal void Save(PublicationIndex index) => Files.ReplaceIndex(Persona.Slot, PublicationIndexCodec.Encode(Persona.Slot, index));

    /// <summary>The names of the entry files in the persona's outbox.</summary>
    internal IReadOnlyList<OutboxEntryName> OutboxEntries() => Files.ListOutbox(Persona.Slot).Entries;

    /// <summary>Every file under the scratch folder, by its path below it.</summary>
    internal IReadOnlyList<string> AllFiles()
    {
        var files = new List<string>();
        if (Directory.Exists(Root))
        {
            foreach (var path in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                files.Add(Path.GetRelativePath(Root, path));
            }
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Key envelopes kept in memory.</summary>
internal sealed class MemoryKeyBlobs : IPersonaKeyBlobStorage
{
    private readonly Dictionary<PersonaSlotId, byte[]> blobs = new();

    /// <summary>Forgets a key, as a deleted or damaged key file would.</summary>
    internal void Forget(PersonaSlotId slot) => blobs.Remove(slot);

    public byte[]? Read(PersonaSlotId slot) => blobs.TryGetValue(slot, out var blob) ? (byte[])blob.Clone() : null;

    public void WriteNew(PersonaSlotId slot, ReadOnlySpan<byte> blob)
    {
        if (!blobs.TryAdd(slot, blob.ToArray()))
        {
            throw new IOException("A key is already held under the slot.");
        }
    }

    public PersonaKeyListing List() => new(new List<PersonaSlotId>(blobs.Keys), 0);
}

/// <summary>A registry kept in memory.</summary>
internal sealed class MemoryRegistry : IPersonaRegistryStorage
{
    private byte[]? bytes;

    public byte[]? Read() => bytes is null ? null : (byte[])bytes.Clone();

    public void Replace(ReadOnlySpan<byte> registry) => bytes = registry.ToArray();
}

/// <summary>
/// A protector that protects nothing, for tests only: the blob is a digest of the context, then the
/// secret masked with that digest, so a blob opens only under its own context.
/// </summary>
internal sealed class MaskingProtector : IPersonaKeyProtector
{
    public string Id => "test.unprotected.v1";

    public byte[] Protect(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> context)
    {
        var tag = SHA256.HashData(context);
        var blob = new byte[tag.Length + secret.Length];
        tag.CopyTo(blob, 0);
        for (var index = 0; index < secret.Length; index++)
        {
            blob[tag.Length + index] = (byte)(secret[index] ^ tag[index % tag.Length]);
        }

        return blob;
    }

    public byte[]? Unprotect(ReadOnlySpan<byte> blob, ReadOnlySpan<byte> context)
    {
        var tag = SHA256.HashData(context);
        if (blob.Length < tag.Length || !blob[..tag.Length].SequenceEqual(tag))
        {
            return null;
        }

        var secret = new byte[blob.Length - tag.Length];
        for (var index = 0; index < secret.Length; index++)
        {
            secret[index] = (byte)(blob[tag.Length + index] ^ tag[index % tag.Length]);
        }

        return secret;
    }
}

/// <summary>No backup format, as the preview has none yet.</summary>
internal sealed class NoBackups : IPersonaBackupCodec
{
    public PersonaBackupInspection Inspect(ReadOnlySpan<byte> backup) => throw new NotSupportedException();

    public byte[] Write(PersonaKeyMaterial material, PersonaBackupSecret secret) => throw new PersonaException(PersonaError.BackupUnsupported, "No backups here.");

    public PersonaKeyMaterial Open(ReadOnlySpan<byte> backup, PersonaBackupSecret secret) => throw new PersonaException(PersonaError.BackupUnsupported, "No backups here.");
}

/// <summary>Real images, made here: 8-bit RGBA PNGs as preparation leaves them, and the same with a chunk it would remove.</summary>
internal static class PreparedPngs
{
    private static readonly uint[] CrcTable = MakeCrcTable();

    /// <summary>A PNG of <paramref name="width"/> by <paramref name="height"/>, 8-bit RGBA, not interlaced: IHDR, IDAT and IEND, and <paramref name="extra"/> before IDAT when given.</summary>
    internal static byte[] Png(int width, int height, byte seed = 7, (string Type, byte[] Data)? extra = null)
    {
        var rows = new byte[height * (1 + (width * 4))];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width * 4; x++)
            {
                rows[(y * (1 + (width * 4))) + 1 + x] = (byte)(seed + (x * 3) + (y * 5));
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(rows);
        }

        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        (header[8], header[9]) = (8, 6);

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(png, "IHDR", header);
        if (extra is { } chunk)
        {
            Chunk(png, chunk.Type, chunk.Data);
        }

        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    /// <summary>A prepared copy of <paramref name="bytes"/>, a PNG of the given size, as preparation declares one: a fresh asset id, its digest, its length and size.</summary>
    internal static PreparedImage Prepared(byte[] bytes, int width, int height) =>
        new(new ImageReference(AssetId.NewId(), SHA256.HashData(bytes), ImageFormat.Png, bytes.Length, width, height), bytes);

    private static void Chunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> field = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(field, (uint)data.Length);
        stream.Write(field);
        var name = new byte[4];
        for (var index = 0; index < 4; index++)
        {
            name[index] = (byte)type[index];
        }

        stream.Write(name);
        stream.Write(data);
        var crc = 0xFFFF_FFFFu;
        foreach (var b in name)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        BinaryPrimitives.WriteUInt32BigEndian(field, crc ^ 0xFFFF_FFFFu);
        stream.Write(field);
    }

    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (var n = 0u; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB8_8320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}

/// <summary>Candidates built as N2-6a builds them, from saved Plates, with real prepared copies of every window drawn.</summary>
internal static class PublicationCandidates
{
    internal static readonly DateTime Now = new(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A Plate of one text and one image, <paramref name="plateId"/>'s, built into a candidate.</summary>
    internal static SnapshotCandidate Simple(Guid? plateId = null, string text = "Hello there")
    {
        var plate = PlateFactory.Create(PlateStartingLayout.Blank, plateId ?? Guid.NewGuid(), "Shared", Now);
        plate.Elements.Add(new TextProfileElement { Text = text, Position = new Vector2(20f, 20f), Size = new Vector2(300f, 60f), ZIndex = 0 });
        plate.Elements.Add(new ImageProfileElement { AssetId = Guid.NewGuid(), Position = new Vector2(40f, 120f), Size = new Vector2(64f, 32f), ZIndex = 1 });
        return Build(plate);
    }

    /// <summary>The candidate a saved Plate builds, with every required window prepared as a real PNG of its size.</summary>
    internal static SnapshotCandidate Build(ProfileDocument plate, FixedMeasurements? measurements = null)
    {
        Assert.True(PlateSnapshotBuilder.TryResolve(plate, measurements ?? new FixedMeasurements(), out var resolved));
        var prepared = new Dictionary<ImageRequirement, ImagePreparation>();
        byte seed = 1;
        foreach (var requirement in resolved.Requirements)
        {
            var png = PreparedPngs.Png(requirement.Window.Width, requirement.Window.Height, seed++);
            prepared[requirement] = ImagePreparation.Prepared(PreparedPngs.Prepared(png, requirement.Window.Width, requirement.Window.Height));
        }

        var result = PlateSnapshotBuilder.Map(resolved, prepared);
        Assert.Empty(result.Problems);
        return result.Candidate!;
    }
}

/// <summary>Measurements with fixed answers, standing in for the renderer's: every image is 64 by 32 unless told otherwise.</summary>
internal sealed class FixedMeasurements : IPlateMeasurements
{
    internal Dictionary<Guid, (int Width, int Height)> Sizes { get; } = new();

    public bool TryMeasureNaturalWidth(TextProfileElement element, out float width)
    {
        width = element.GetDisplayText().Length * element.FontSize * 0.5f;
        return true;
    }

    public bool TryGetDisplayOverride(ProfileDocument plate, TextProfileElement element, out string? display)
    {
        display = null;
        return true;
    }

    public bool TryGetImageSize(Guid image, out int width, out int height)
    {
        (width, height) = Sizes.TryGetValue(image, out var size) ? size : (64, 32);
        return true;
    }
}
