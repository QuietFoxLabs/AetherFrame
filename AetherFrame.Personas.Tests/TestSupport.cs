using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Signing;

namespace AetherFrame.Personas.Tests;

/// <summary>
/// Synthetic keys: fresh random P-256 keys, or fixed textbook scalars (1, n - 1) that no generator
/// would produce, that exist only in this test process and are never written anywhere. None of them
/// can be mistaken for a player's key, because no player key exists.
/// </summary>
internal static class SyntheticKeys
{
    /// <summary>The order of the P-256 base point.</summary>
    public static readonly BigInteger Order = BigInteger.Parse("0FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551", NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

    /// <summary>The prime of the P-256 field.</summary>
    public static readonly BigInteger Prime = BigInteger.Parse("0FFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF", NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

    /// <summary>The P-256 base point G, as the specification gives it.</summary>
    public static PersonaPublicKey BasePoint { get; } = PersonaPublicKey.FromBytes(Convert.FromHexString(
        "04" +
        "6B17D1F2E12C4247F8BCE6E563A440F277037D812DEB33A0F4A13945D898C296" +
        "4FE342E2FE1A7F9B8EE7EB4A7C0F9E162BCE33576B315ECECBB6406837BF51F5"));

    /// <summary>-G: the point whose private scalar is n - 1 (same x, y replaced by p - y).</summary>
    public static PersonaPublicKey NegatedBasePoint { get; } = Negate(BasePoint);

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

    public static PersonaKeyMaterial Material() => PersonaKeyMaterial.Generate();

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

    /// <summary>A copy of the private scalar, which the test zeroes.</summary>
    public static byte[] Scalar(ECDsa key)
    {
        var parameters = key.ExportParameters(includePrivateParameters: true);
        return parameters.D!;
    }

    /// <summary><paramref name="value"/> as 32 big-endian bytes (it must be below 2^256).</summary>
    public static byte[] Fixed32(BigInteger value)
    {
        var bytes = new byte[32];
        var raw = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        raw.CopyTo(bytes, 32 - raw.Length);
        return bytes;
    }

    /// <summary>scalar·G for a scalar from 1 to n - 1, derived by the platform from the scalar alone.</summary>
    public static PersonaPublicKey PointFor(BigInteger scalar)
    {
        var d = Fixed32(scalar);
        try
        {
            using var key = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = d });
            return PersonaPublicKey.FromEcdsa(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(d);
        }
    }

    public static PersonaPublicKey Negate(PersonaPublicKey point)
    {
        var bytes = point.ToArray();
        var y = new BigInteger(bytes.AsSpan(33, 32), isUnsigned: true, isBigEndian: true);
        Fixed32(Prime - y).CopyTo(bytes, 33);
        return PersonaPublicKey.FromBytes(bytes);
    }
}

/// <summary>
/// In-memory custody for tests: keys live in a dictionary and vanish with the process. It stores
/// nothing, protects nothing and is not a vault. It follows the store contract (a copy is kept, a
/// held slot is refused, a failure holds nothing) unless told to break it, records every call so
/// tests can assert that the manager never touches a key for an operation that does not need one,
/// and can be told to treat a slot as locked, to hand out the wrong persona's key, or to fail in
/// each of the ways a real store might. Not thread-safe: the manager serializes its store calls,
/// which the concurrency tests rely on.
/// </summary>
internal sealed class InMemoryPersonaKeyStore : IPersonaKeyStore
{
    private readonly Dictionary<PersonaSlotId, PersonaKeyMaterial> keys = new();
    private readonly HashSet<PersonaSlotId> locked = new();

    public List<string> Calls { get; } = new();

    /// <summary>The platform key the next <see cref="GenerateKey"/> copies instead of a fresh one; used once, then disposed here.</summary>
    public Func<ECDsa>? NextKey { get; set; }

    /// <summary>Thrown by the next <see cref="GenerateKey"/>.</summary>
    public Exception? FailNextGenerate { get; set; }

    /// <summary>When set, <see cref="GenerateKey"/> returns null: a store breaking its contract.</summary>
    public bool GenerateNothing { get; set; }

    /// <summary>Thrown by the next <see cref="AddKey"/> before anything is held: an atomic failure.</summary>
    public Exception? FailNextAdd { get; set; }

    /// <summary>Thrown by the next <see cref="AddKey"/> after the key is held: a store breaking its atomicity rule.</summary>
    public Exception? FailNextAddAfterCommit { get; set; }

    /// <summary>When set, the next <see cref="AddKey"/> holds this key instead of the one it was given: a faulty store.</summary>
    public Func<ECDsa>? SubstituteNextAdd { get; set; }

    /// <summary>Runs in every <see cref="AddKey"/> just before the key is committed: stands for a store that takes its time.</summary>
    public Action<PersonaSlotId>? BeforeCommit { get; set; }

    /// <summary>When set, <see cref="OpenSigner"/> answers with this instead of a signer over the held key.</summary>
    public Func<PersonaSlotId, IPersonaSigner?>? SignerOverride { get; set; }

    /// <summary>Slots whose opens are answered with another slot's key.</summary>
    public Dictionary<PersonaSlotId, PersonaSlotId> Impersonate { get; } = new();

    /// <summary>Every material this store handed out (generated or opened), to check the caller disposes it.</summary>
    public List<PersonaKeyMaterial> HandedOut { get; } = new();

    /// <summary>Every material passed to <see cref="AddKey"/>, to check the store never keeps the caller's object.</summary>
    public List<PersonaKeyMaterial> Received { get; } = new();

    public int Count => keys.Count;

    public int CallsTo(string name) => Calls.FindAll(c => c == name).Count;

    public PersonaKeyMaterial GenerateKey()
    {
        Calls.Add(nameof(GenerateKey));
        if (FailNextGenerate is { } failure)
        {
            FailNextGenerate = null;
            throw failure;
        }

        if (GenerateNothing)
        {
            return null!;
        }

        PersonaKeyMaterial material;
        if (NextKey is { } next)
        {
            NextKey = null;
            using var key = next();
            material = PersonaKeyMaterial.FromEcdsa(key);
        }
        else
        {
            material = PersonaKeyMaterial.Generate();
        }

        HandedOut.Add(material);
        return material;
    }

    public void AddKey(PersonaSlotId slot, PersonaKeyMaterial material)
    {
        Calls.Add(nameof(AddKey));
        Received.Add(material);
        if (FailNextAdd is { } failure)
        {
            FailNextAdd = null;
            throw failure;
        }

        if (keys.ContainsKey(slot))
        {
            throw new InvalidOperationException("This store already holds a key under that slot and never replaces one.");
        }

        BeforeCommit?.Invoke(slot);
        PersonaKeyMaterial copy;
        if (SubstituteNextAdd is { } substitute)
        {
            SubstituteNextAdd = null;
            using var other = substitute();
            copy = PersonaKeyMaterial.FromEcdsa(other);
        }
        else
        {
            copy = material.Copy();
        }

        keys.Add(slot, copy);
        if (FailNextAddAfterCommit is { } late)
        {
            FailNextAddAfterCommit = null;
            throw late;
        }
    }

    public IPersonaSigner? OpenSigner(PersonaSlotId slot)
    {
        Calls.Add(nameof(OpenSigner));
        if (SignerOverride is { } signer)
        {
            return signer(slot);
        }

        return Resolve(slot)?.CreateSigner();
    }

    public PersonaKeyMaterial? OpenKey(PersonaSlotId slot)
    {
        Calls.Add(nameof(OpenKey));
        var material = Resolve(slot)?.Copy();
        if (material is not null)
        {
            HandedOut.Add(material);
        }

        return material;
    }

    public void Lock(PersonaSlotId slot) => locked.Add(slot);

    public void Unlock(PersonaSlotId slot) => locked.Remove(slot);

    public bool Holds(PersonaSlotId slot) => keys.ContainsKey(slot);

    public IReadOnlyCollection<PersonaSlotId> Slots => keys.Keys;

    /// <summary>The material held for a slot, to check it is the same object after an operation that must not touch it.</summary>
    public PersonaKeyMaterial Held(PersonaSlotId slot) => keys[slot];

    private PersonaKeyMaterial? Resolve(PersonaSlotId slot)
    {
        var actual = Impersonate.TryGetValue(slot, out var other) ? other : slot;
        return !locked.Contains(actual) && keys.TryGetValue(actual, out var material) ? material : null;
    }
}

/// <summary>
/// A test double for the backup seam, and deliberately not a file format. The bytes it produces are
/// a magic, a version byte and a random 16-byte handle into this object's memory, where a copy of
/// the material and of the secret's characters are kept for the duration of the test. No key
/// material and no secret is in the bytes, nothing is encrypted, and nothing could be restored by
/// any other process. It exists so the manager's inspection, duplicate and refusal paths can be
/// exercised without a format that has not been approved. It records what it was shown and can be
/// told to misbehave the ways a faulty codec might. Thread-safe enough for the concurrency tests:
/// its state is under a lock.
/// </summary>
internal sealed class HandleBackupCodec : IPersonaBackupCodec
{
    public const byte SupportedVersion = 1;
    public const int Length = 6 + 1 + 16;

    private readonly object sync = new();
    private readonly Dictionary<string, (PersonaKeyMaterial Material, char[] Secret)> handles = new();

    private static ReadOnlySpan<byte> Magic => "AFTEST"u8;

    public int InspectCalls { get; private set; }

    public int WriteCalls { get; private set; }

    public int OpenCalls { get; private set; }

    /// <summary>Copies of the bytes each <see cref="Inspect"/> saw.</summary>
    public List<byte[]> Inspected { get; } = new();

    /// <summary>Copies of the bytes each <see cref="Open"/> saw.</summary>
    public List<byte[]> Opened { get; } = new();

    /// <summary>Every material <see cref="Open"/> handed out, to check the caller disposes it.</summary>
    public List<PersonaKeyMaterial> HandedOut { get; } = new();

    /// <summary>When set, <see cref="Inspect"/> answers with this (given a copy of the bytes) instead of classifying them.</summary>
    public Func<byte[], PersonaBackupInspection?>? InspectOverride { get; set; }

    /// <summary>Runs inside <see cref="Inspect"/> after the bytes were read: stands for another thread writing to the caller's buffer.</summary>
    public Action? DuringInspect { get; set; }

    /// <summary>Runs inside <see cref="Inspect"/> before the bytes are read: stands for another thread writing to the caller's buffer first.</summary>
    public Action? BeforeInspectRead { get; set; }

    /// <summary>When set, <see cref="Open"/> returns null: a codec breaking its contract.</summary>
    public bool OpenNothing { get; set; }

    /// <summary>When set, <see cref="Open"/> refuses with this error once it has the secret.</summary>
    public PersonaError? FailOpenWith { get; set; }

    /// <summary>When set, <see cref="Open"/> decodes a scalar and a point that do not belong together, as a damaged or forged backup would give.</summary>
    public bool OpenAMismatchedPair { get; set; }

    /// <summary>Thrown by the next <see cref="Write"/>.</summary>
    public Exception? FailNextWrite { get; set; }

    public PersonaBackupInspection Inspect(ReadOnlySpan<byte> backup)
    {
        BeforeInspectRead?.Invoke();
        var seen = backup.ToArray();
        Func<byte[], PersonaBackupInspection?>? inspect;
        Action? during;
        lock (sync)
        {
            InspectCalls++;
            Inspected.Add(seen);
            inspect = InspectOverride;
            during = DuringInspect;
        }

        var inspection = inspect is not null ? inspect(seen) : Classify(seen);
        during?.Invoke();
        return inspection!;
    }

    public byte[] Write(PersonaKeyMaterial material, PersonaBackupSecret secret)
    {
        lock (sync)
        {
            WriteCalls++;
            if (FailNextWrite is { } failure)
            {
                FailNextWrite = null;
                throw failure;
            }

            var handle = RandomNumberGenerator.GetBytes(16);
            handles[Convert.ToHexString(handle)] = (material.Copy(), secret.Text.ToArray());
            var bytes = new byte[Length];
            Magic.CopyTo(bytes);
            bytes[6] = SupportedVersion;
            handle.CopyTo(bytes, 7);
            return bytes;
        }
    }

    public PersonaKeyMaterial Open(ReadOnlySpan<byte> backup, PersonaBackupSecret secret)
    {
        var seen = backup.ToArray();
        lock (sync)
        {
            OpenCalls++;
            Opened.Add(seen);
            if (OpenNothing)
            {
                return null!;
            }

            if (FailOpenWith is { } error)
            {
                throw new PersonaException(error, "The test codec refuses after using the secret.");
            }

            if (OpenAMismatchedPair)
            {
                using var mine = SyntheticKeys.Create();
                using var theirs = SyntheticKeys.Create();
                var scalar = SyntheticKeys.Scalar(mine);
                try
                {
                    return PersonaKeyMaterial.Import(scalar, PersonaPublicKey.FromEcdsa(theirs));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(scalar);
                }
            }

            var inspection = Classify(seen);
            switch (inspection.Status)
            {
                case PersonaBackupStatus.Malformed:
                    throw new PersonaException(PersonaError.BackupMalformed, "Not a test backup.");
                case PersonaBackupStatus.UnsupportedVersion:
                    throw new PersonaException(PersonaError.BackupUnsupported, "A test backup of another version.");
            }

            if (!handles.TryGetValue(Convert.ToHexString(seen, 7, 16), out var entry) || !entry.Secret.AsSpan().SequenceEqual(secret.Text))
            {
                throw new PersonaException(PersonaError.BackupCannotBeOpened, "The secret is wrong or the backup is damaged.");
            }

            var material = entry.Material.Copy();
            HandedOut.Add(material);
            return material;
        }
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

    /// <summary>
    /// An inspection built without its constructor, so it can carry what the constructor refuses
    /// (an undefined status, an incoherent version): the only way a codec could hand the manager one.
    /// </summary>
    public static PersonaBackupInspection Forged(PersonaBackupStatus status, int formatVersion)
    {
        var inspection = (PersonaBackupInspection)RuntimeHelpers.GetUninitializedObject(typeof(PersonaBackupInspection));
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        typeof(PersonaBackupInspection).GetField("<Status>k__BackingField", flags)!.SetValue(inspection, status);
        typeof(PersonaBackupInspection).GetField("<FormatVersion>k__BackingField", flags)!.SetValue(inspection, formatVersion);
        return inspection;
    }

    private static PersonaBackupInspection Classify(ReadOnlySpan<byte> backup)
    {
        if (backup.Length != Length || !backup.Slice(0, Magic.Length).SequenceEqual(Magic))
        {
            return new PersonaBackupInspection(PersonaBackupStatus.Malformed, 0);
        }

        var version = backup[6];
        return version switch
        {
            0 => new PersonaBackupInspection(PersonaBackupStatus.Malformed, 0),
            SupportedVersion => new PersonaBackupInspection(PersonaBackupStatus.Supported, version),
            _ => new PersonaBackupInspection(PersonaBackupStatus.UnsupportedVersion, version),
        };
    }
}

/// <summary>A signer whose reported key can be changed after it was handed out: a faulty store's signer.</summary>
internal sealed class ShiftingSigner : IPersonaSigner, IDisposable
{
    private readonly EcdsaPersonaSigner inner;
    private PersonaPublicKey publicKey;

    public ShiftingSigner(EcdsaPersonaSigner inner)
    {
        this.inner = inner;
        publicKey = inner.PublicKey;
    }

    public PersonaPublicKey PublicKey
    {
        get => ThrowOnPublicKey ? throw new CryptographicException("The key cannot be read.") : publicKey;
        set => publicKey = value;
    }

    /// <summary>When set, reading <see cref="PublicKey"/> throws, as a store signer over a damaged key might.</summary>
    public bool ThrowOnPublicKey { get; set; }

    public int SignCalls { get; private set; }

    public bool Disposed { get; private set; }

    public ProtocolSignature Sign(SigningInput input)
    {
        SignCalls++;
        return inner.Sign(input);
    }

    public void Dispose()
    {
        Disposed = true;
        inner.Dispose();
    }
}

/// <summary>
/// A store signer whose <see cref="Sign"/> announces that it started and then waits to be released,
/// so a test can hold a signature in flight and see what else can happen meanwhile. It records the
/// order of events and whether it was disposed while a signature was running.
/// </summary>
internal sealed class BlockingSigner : IPersonaSigner, IDisposable
{
    private readonly EcdsaPersonaSigner inner;
    private int signing;

    public BlockingSigner(EcdsaPersonaSigner inner)
    {
        this.inner = inner;
    }

    public ManualResetEventSlim Entered { get; } = new();

    public ManualResetEventSlim Release { get; } = new();

    public System.Collections.Concurrent.ConcurrentQueue<string> Events { get; } = new();

    public bool DisposedWhileSigning { get; private set; }

    public PersonaPublicKey PublicKey => inner.PublicKey;

    public ProtocolSignature Sign(SigningInput input)
    {
        Interlocked.Increment(ref signing);
        Events.Enqueue("sign-start");
        Entered.Set();
        if (!Release.Wait(TimeSpan.FromSeconds(20)))
        {
            throw new TimeoutException("The test never released the signature.");
        }

        var signature = inner.Sign(input);
        Events.Enqueue("sign-end");
        Interlocked.Decrement(ref signing);
        return signature;
    }

    public void Dispose()
    {
        DisposedWhileSigning |= Volatile.Read(ref signing) != 0;
        Events.Enqueue("dispose");
        inner.Dispose();
    }
}

/// <summary>
/// A caller's platform key that remembers every private scalar array it hands out, so a test can
/// check that whoever read it zeroed it. The key itself is an ordinary synthetic P-256 key.
/// </summary>
internal sealed class ScalarWatchingEcdsa : ECDsa
{
    private readonly ECDsa inner = SyntheticKeys.Create();

    public ScalarWatchingEcdsa()
    {
        KeySizeValue = 256;
    }

    public List<byte[]> ScalarsHandedOut { get; } = new();

    public PersonaPublicKey PublicKey => PersonaPublicKey.FromEcdsa(inner);

    public override ECParameters ExportParameters(bool includePrivateParameters)
    {
        var parameters = inner.ExportParameters(includePrivateParameters);
        if (parameters.D is not null)
        {
            ScalarsHandedOut.Add(parameters.D);
        }

        return parameters;
    }

    public override byte[] SignHash(byte[] hash) => inner.SignHash(hash);

    public override bool VerifyHash(byte[] hash, byte[] signature) => inner.VerifyHash(hash, signature);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>Sample protocol documents, patterned so they are obviously synthetic.</summary>
internal static class Documents
{
    public static readonly ProfileId Profile = ProfileId.Parse("prf_a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1");

    public static AetherFrame.Protocol.Remote.ProfileRetraction Retraction() => new(Profile, 1_700_000_100);

    public static byte[] SignedRetraction(IPersonaSigner signer) => AetherFrame.Protocol.Documents.SignedDocumentCodec.Sign(Retraction(), signer);
}

/// <summary>Whether a material has been disposed, read the only way its public surface allows.</summary>
internal static class Disposal
{
    public static bool IsDisposed(PersonaKeyMaterial material)
    {
        try
        {
            material.CreateSigner().Dispose();
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }
}
