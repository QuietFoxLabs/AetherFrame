using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Signing;

namespace AetherFrame.Personas.Tests;

/// <summary>
/// Synthetic keys: fresh random P-256 keys that exist only in this test process and are never
/// written anywhere. None of them can be mistaken for a player's key, because no player key exists.
/// </summary>
internal static class SyntheticKeys
{
    /// <summary>The order of the P-256 base point, big-endian.</summary>
    public static ReadOnlySpan<byte> GroupOrder =>
    [
        0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xBC, 0xE6, 0xFA, 0xAD, 0xA7, 0x17, 0x9E, 0x84, 0xF3, 0xB9, 0xCA, 0xC2, 0xFC, 0x63, 0x25, 0x51,
    ];

    public static ECDsa Create() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public static ECDsa Copy(ECDsa key)
    {
        var parameters = key.ExportParameters(includePrivateParameters: true);
        try
        {
            return ECDsa.Create(parameters);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(parameters.D);
        }
    }

    public static PersonaKeyMaterial Material() => new(Create());

    /// <summary>The same point with no private scalar.</summary>
    public static ECDsa PublicOnly(ECDsa key) => ECDsa.Create(key.ExportParameters(includePrivateParameters: false));

    public static string PublicHex(PersonaPublicKey key) => Convert.ToHexStringLower(key.Bytes);

    /// <summary>The private scalar as hex, for asserting that some text does not contain it.</summary>
    public static string PrivateHex(ECDsa key)
    {
        var parameters = key.ExportParameters(includePrivateParameters: true);
        try
        {
            return Convert.ToHexStringLower(parameters.D!);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(parameters.D);
        }
    }
}

/// <summary>
/// In-memory custody for tests: keys live in a dictionary and vanish with the process. It stores
/// nothing, protects nothing and is not a vault. It records every call so tests can assert that the
/// manager never touches a key for an operation that does not need one, and it can be told to
/// treat a slot as locked (as a protected store would for a key made under another account) or to
/// hand out the wrong persona's key (a store fault the manager must catch). Not thread-safe: the
/// manager serializes its store calls, which the concurrency test relies on.
/// </summary>
internal sealed class InMemoryPersonaKeyStore : IPersonaKeyStore
{
    private readonly Dictionary<PersonaSlotId, PersonaKeyMaterial> keys = new();
    private readonly HashSet<PersonaSlotId> locked = new();

    public List<string> Calls { get; } = new();

    /// <summary>The platform key the next <see cref="CreateKey"/> wraps instead of a fresh one; used once.</summary>
    public Func<ECDsa>? NextKey { get; set; }

    /// <summary>Slots whose opens are answered with another slot's key.</summary>
    public Dictionary<PersonaSlotId, PersonaSlotId> Impersonate { get; } = new();

    public int Count => keys.Count;

    public int CallsTo(string name) => Calls.FindAll(c => c == name).Count;

    public PersonaPublicKey CreateKey(PersonaSlotId slot)
    {
        Calls.Add(nameof(CreateKey));
        var key = NextKey?.Invoke() ?? SyntheticKeys.Create();
        NextKey = null;
        var material = new PersonaKeyMaterial(key);
        keys.Add(slot, material);
        return material.PublicKey;
    }

    public PersonaPublicKey AdoptKey(PersonaSlotId slot, PersonaKeyMaterial material)
    {
        Calls.Add(nameof(AdoptKey));
        keys.Add(slot, material);
        return material.PublicKey;
    }

    public IPersonaSigner? OpenSigner(PersonaSlotId slot)
    {
        Calls.Add(nameof(OpenSigner));
        return Resolve(slot)?.CreateSigner();
    }

    public PersonaKeyMaterial? OpenKey(PersonaSlotId slot)
    {
        Calls.Add(nameof(OpenKey));
        return Resolve(slot)?.Copy();
    }

    public void Lock(PersonaSlotId slot) => locked.Add(slot);

    public void Unlock(PersonaSlotId slot) => locked.Remove(slot);

    public bool Holds(PersonaSlotId slot) => keys.ContainsKey(slot);

    /// <summary>The material held for a slot, to check it is the same object after an operation that must not touch it.</summary>
    public PersonaKeyMaterial Held(PersonaSlotId slot) => keys[slot];

    private PersonaKeyMaterial? Resolve(PersonaSlotId slot)
    {
        var actual = Impersonate.TryGetValue(slot, out var other) ? other : slot;
        return !locked.Contains(actual) && keys.TryGetValue(actual, out var material) ? material : null;
    }
}

/// <summary>A store whose adoption fails, for the manager's rollback path.</summary>
internal sealed class RefusingKeyStore : IPersonaKeyStore
{
    private readonly InMemoryPersonaKeyStore inner = new();

    public PersonaPublicKey CreateKey(PersonaSlotId slot) => inner.CreateKey(slot);

    public PersonaPublicKey AdoptKey(PersonaSlotId slot, PersonaKeyMaterial material) => throw new InvalidOperationException("This store refuses every adoption.");

    public IPersonaSigner? OpenSigner(PersonaSlotId slot) => inner.OpenSigner(slot);

    public PersonaKeyMaterial? OpenKey(PersonaSlotId slot) => inner.OpenKey(slot);
}

/// <summary>
/// A test double for the backup seam, and deliberately not a file format. The bytes it produces are
/// a magic, a version byte and a random 16-byte handle into this object's memory, where a copy of
/// the material and of the secret's characters are kept for the duration of the test. No key
/// material and no secret is in the bytes, nothing is encrypted, and nothing could be restored by
/// any other process. It exists so the manager's inspection, duplicate and refusal paths can be
/// exercised without a format that has not been approved.
/// </summary>
internal sealed class HandleBackupCodec : IPersonaBackupCodec
{
    public const byte SupportedVersion = 1;
    public const int Length = 6 + 1 + 16;

    private readonly Dictionary<string, (PersonaKeyMaterial Material, char[] Secret)> handles = new();

    private static ReadOnlySpan<byte> Magic => "AFTEST"u8;

    public int InspectCalls { get; private set; }

    public int WriteCalls { get; private set; }

    public int OpenCalls { get; private set; }

    public PersonaBackupInspection Inspect(ReadOnlySpan<byte> backup)
    {
        InspectCalls++;
        return Classify(backup);
    }

    public byte[] Write(PersonaKeyMaterial material, PersonaBackupSecret secret)
    {
        WriteCalls++;
        var handle = RandomNumberGenerator.GetBytes(16);
        handles[Convert.ToHexString(handle)] = (material.Copy(), secret.Text.ToArray());
        var bytes = new byte[Length];
        Magic.CopyTo(bytes);
        bytes[6] = SupportedVersion;
        handle.CopyTo(bytes, 7);
        return bytes;
    }

    public PersonaKeyMaterial Open(ReadOnlySpan<byte> backup, PersonaBackupSecret secret)
    {
        OpenCalls++;
        var inspection = Classify(backup);
        switch (inspection.Status)
        {
            case PersonaBackupStatus.Malformed:
                throw new PersonaException(PersonaError.BackupMalformed, "Not a test backup.");
            case PersonaBackupStatus.UnsupportedVersion:
                throw new PersonaException(PersonaError.BackupUnsupported, "A test backup of another version.");
        }

        if (!handles.TryGetValue(Convert.ToHexString(backup.Slice(7)), out var entry) || !entry.Secret.AsSpan().SequenceEqual(secret.Text))
        {
            throw new PersonaException(PersonaError.BackupCannotBeOpened, "The secret is wrong or the backup is damaged.");
        }

        return entry.Material.Copy();
    }

    public static byte[] WithVersion(byte[] backup, byte version)
    {
        var copy = (byte[])backup.Clone();
        copy[6] = version;
        return copy;
    }

    public static byte[] Damaged(byte[] backup)
    {
        var copy = (byte[])backup.Clone();
        copy[^1] ^= 0x01;
        return copy;
    }

    private static PersonaBackupInspection Classify(ReadOnlySpan<byte> backup)
    {
        if (backup.Length != Length || !backup.Slice(0, Magic.Length).SequenceEqual(Magic))
        {
            return new PersonaBackupInspection(PersonaBackupStatus.Malformed, 0);
        }

        var version = backup[6];
        return version == SupportedVersion
            ? new PersonaBackupInspection(PersonaBackupStatus.Supported, version)
            : new PersonaBackupInspection(PersonaBackupStatus.UnsupportedVersion, version);
    }
}

/// <summary>Sample protocol documents, patterned so they are obviously synthetic.</summary>
internal static class Documents
{
    public static readonly ProfileId Profile = ProfileId.Parse("prf_a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1");

    public static AetherFrame.Protocol.Remote.ProfileRetraction Retraction() => new(Profile, 1_700_000_100);

    public static byte[] SignedRetraction(IPersonaSigner signer) => AetherFrame.Protocol.Documents.SignedDocumentCodec.Sign(Retraction(), signer);
}
