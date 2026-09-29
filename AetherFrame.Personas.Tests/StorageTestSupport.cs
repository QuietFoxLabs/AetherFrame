using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using AetherFrame.Personas.Storage;

namespace AetherFrame.Personas.Tests;

/// <summary>
/// Key blob storage for tests: a dictionary that vanishes with the process. It follows the storage
/// contract (atomic, never replaces, holds exactly what it was given) unless told to break it, and
/// records every call.
/// </summary>
internal sealed class InMemoryKeyBlobStorage : IPersonaKeyBlobStorage
{
    private readonly Dictionary<PersonaSlotId, byte[]> blobs = new();

    public List<string> Calls { get; } = new();

    /// <summary>Thrown by the next <see cref="WriteNew"/> before anything is held.</summary>
    public Exception? FailNextWrite { get; set; }

    /// <summary>Thrown by the next <see cref="WriteNew"/> after the blob is held: a storage breaking atomicity.</summary>
    public Exception? FailNextWriteAfterHolding { get; set; }

    /// <summary>When set, the next <see cref="WriteNew"/> holds this instead of the blob it was given: a faulty storage.</summary>
    public Func<byte[], byte[]>? SubstituteNextWrite { get; set; }

    /// <summary>Thrown by every <see cref="Read"/> while set.</summary>
    public Exception? ReadFailure { get; set; }

    public int Count => blobs.Count;

    public IEnumerable<PersonaSlotId> Slots => blobs.Keys;

    public byte[] Held(PersonaSlotId slot) => (byte[])blobs[slot].Clone();

    /// <summary>Puts arbitrary bytes under a slot, as a damaged or foreign file would.</summary>
    public void Plant(PersonaSlotId slot, byte[] bytes) => blobs[slot] = (byte[])bytes.Clone();

    public byte[]? Read(PersonaSlotId slot)
    {
        Calls.Add(nameof(Read));
        if (ReadFailure is { } failure)
        {
            throw failure;
        }

        return blobs.TryGetValue(slot, out var blob) ? (byte[])blob.Clone() : null;
    }

    public void WriteNew(PersonaSlotId slot, ReadOnlySpan<byte> blob)
    {
        Calls.Add(nameof(WriteNew));
        if (FailNextWrite is { } failure)
        {
            FailNextWrite = null;
            throw failure;
        }

        if (blobs.ContainsKey(slot))
        {
            throw new InvalidOperationException("held");
        }

        var bytes = blob.ToArray();
        if (SubstituteNextWrite is { } substitute)
        {
            SubstituteNextWrite = null;
            bytes = substitute(bytes);
        }

        blobs[slot] = bytes;
        if (FailNextWriteAfterHolding is { } late)
        {
            FailNextWriteAfterHolding = null;
            throw late;
        }
    }
}

/// <summary>
/// A protector that protects nothing, for tests only: the blob is a tag of the context followed by
/// the secret masked with a keystream derived from the context, so a test can see that the plain
/// scalar is not in an envelope and that a blob under another context does not open, while nothing
/// here is secret from anyone who can read the blob. Its id says so. It records the arrays it
/// handed out so tests can check that custody code zeroes them, and it can be told to misbehave in
/// each way a platform protector might.
/// </summary>
internal sealed class FakeProtector : IPersonaKeyProtector
{
    public const string DefaultId = "test.unprotected.v1";

    public FakeProtector(string id = DefaultId)
    {
        Id = id;
    }

    public string Id { get; }

    public List<string> Calls { get; } = new();

    /// <summary>Every array <see cref="Unprotect"/> returned, to check that the caller zeroed it.</summary>
    public List<byte[]> HandedOut { get; } = new();

    /// <summary>Thrown by the next <see cref="Protect"/>.</summary>
    public Exception? FailNextProtect { get; set; }

    /// <summary>When set, the next <see cref="Protect"/> returns this instead of a real blob.</summary>
    public byte[]? NextBlob { get; set; }

    /// <summary>When set, every <see cref="Unprotect"/> returns null: a key locked on this account.</summary>
    public bool Locked { get; set; }

    /// <summary>Thrown by every <see cref="Unprotect"/> while set.</summary>
    public Exception? UnprotectFailure { get; set; }

    /// <summary>When set, the next <see cref="Unprotect"/> returns this instead of the secret: a protector that lies.</summary>
    public byte[]? SubstituteNextUnprotect { get; set; }

    public byte[] Protect(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> context)
    {
        Calls.Add(nameof(Protect));
        if (FailNextProtect is { } failure)
        {
            FailNextProtect = null;
            throw failure;
        }

        if (NextBlob is { } planted)
        {
            NextBlob = null;
            return planted;
        }

        var tag = Tag(context);
        var blob = new byte[tag.Length + secret.Length];
        tag.CopyTo(blob, 0);
        Mask(secret, context, blob.AsSpan(tag.Length));
        return blob;
    }

    public byte[]? Unprotect(ReadOnlySpan<byte> blob, ReadOnlySpan<byte> context)
    {
        Calls.Add(nameof(Unprotect));
        if (UnprotectFailure is { } failure)
        {
            throw failure;
        }

        if (Locked)
        {
            return null;
        }

        if (SubstituteNextUnprotect is { } substitute)
        {
            SubstituteNextUnprotect = null;
            HandedOut.Add(substitute);
            return substitute;
        }

        var tag = Tag(context);
        if (blob.Length <= tag.Length || !blob[..tag.Length].SequenceEqual(tag))
        {
            return null;
        }

        var secret = new byte[blob.Length - tag.Length];
        Mask(blob[tag.Length..], context, secret);
        HandedOut.Add(secret);
        return secret;
    }

    private static byte[] Tag(ReadOnlySpan<byte> context) => SHA256.HashData(context)[..16];

    private static void Mask(ReadOnlySpan<byte> input, ReadOnlySpan<byte> context, Span<byte> output)
    {
        var seed = new byte[context.Length + 4];
        context.CopyTo(seed);
        "mask"u8.CopyTo(seed.AsSpan(context.Length));
        var stream = SHA256.HashData(seed);
        for (var index = 0; index < input.Length; index++)
        {
            output[index] = (byte)(input[index] ^ stream[index % stream.Length]);
        }
    }
}

/// <summary>A directory under the system temporary folder that is deleted with the test.</summary>
internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AetherFrame.Personas.Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
