using System;
using System.Security.Cryptography;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Signing;

namespace AetherFrame.Personas.Storage;

/// <summary>
/// The key store core (docs/networking/NETWORK1_KeyStoreCore.md): custody of persona private keys as
/// protected envelopes, one per slot, in a storage the plugin supplies, protected by a protector the
/// plugin supplies. It holds no key itself and caches nothing private: every open reads the
/// envelope, asks the protector for the scalar, rebuilds the key through
/// <see cref="PersonaKeyMaterial"/>'s checks, and zeroes the scalar. What it guarantees is the
/// store contract (<see cref="IPersonaKeyStore"/>): it commits exactly the key it was given and
/// verifies that before anything is durable, it never replaces, it is atomic, and it never retains
/// the caller's material. What it does not guarantee is protection: that is the protector's claim,
/// and only a reviewed protector may make one.
/// <para>
/// Not thread-safe; the <see cref="PersonaManager"/> serializes its calls. Each call does one
/// storage read or write and one protector call on a few hundred bytes, so the manager's lock is
/// held briefly; a protector that prompts or blocks would change that and is not allowed.
/// </para>
/// </summary>
public sealed class ProtectedPersonaKeyStore : IPersonaKeyStore
{
    private readonly IPersonaKeyBlobStorage storage;
    private readonly IPersonaKeyProtector protector;
    private readonly Action<string>? report;

    /// <summary>
    /// A store over <paramref name="storage"/> and <paramref name="protector"/>. Every line handed to
    /// <paramref name="report"/> (when given) explains a key that could not be opened; it names the
    /// slot, a local random handle, and never an identity, a label or any key bytes.
    /// </summary>
    /// <exception cref="ArgumentException">The protector's id is not 1 to 64 lowercase letters, digits, dots and hyphens.</exception>
    public ProtectedPersonaKeyStore(IPersonaKeyBlobStorage storage, IPersonaKeyProtector protector, Action<string>? report = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(protector);
        if (!ProtectedKeyEnvelope.IsValidProtectorId(protector.Id))
        {
            throw new ArgumentException("A protector id is 1 to 64 lowercase letters, digits, dots and hyphens.", nameof(protector));
        }

        this.storage = storage;
        this.protector = protector;
        this.report = report;
    }

    /// <inheritdoc />
    public PersonaKeyMaterial GenerateKey() => PersonaKeyMaterial.Generate();

    /// <summary>
    /// Commits a copy of the key under <paramref name="slot"/>: the scalar is protected under the
    /// envelope's header, the envelope is decoded, unprotected and imported again to prove it is
    /// exactly this key, and only then is it written; the storage's copy is read back and compared.
    /// The caller's material is never retained and stays the caller's to dispose.
    /// </summary>
    /// <exception cref="InvalidOperationException">The slot already holds a key, which is left as it was.</exception>
    /// <exception cref="PersonaException"><see cref="PersonaError.CustodyFailed"/>: the protector or the storage refused, or the envelope did not prove to hold this key. Nothing is held.</exception>
    public void AddKey(PersonaSlotId slot, PersonaKeyMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);
        if (slot.IsEmpty)
        {
            throw new ArgumentException("The empty slot never holds a key.", nameof(slot));
        }

        if (ReadOrFail(slot) is not null)
        {
            throw new InvalidOperationException("The slot already holds a key; a store never replaces one.");
        }

        var parameters = material.ExportPrivateParameters();
        var scalar = parameters.D;
        try
        {
            if (scalar is not { Length: PersonaKeyMaterial.ScalarLength })
            {
                throw Failed("The material exported no private scalar.");
            }

            var header = ProtectedKeyEnvelope.EncodeHeader(slot, protector.Id, material.PublicKey);
            byte[] blob;
            try
            {
                blob = protector.Protect(scalar, header);
            }
            catch (Exception e)
            {
                throw Failed("The protector refused to protect the key.", e);
            }

            if (blob is not { Length: > 0 and <= ProtectedKeyEnvelope.MaxBlobLength })
            {
                throw Failed("The protector produced no usable blob.");
            }

            var envelope = ProtectedKeyEnvelope.Encode(header, blob);
            ProveHoldsKey(envelope, slot, material.PublicKey, scalar);

            try
            {
                storage.WriteNew(slot, envelope);
            }
            catch (Exception e)
            {
                throw Failed("The storage could not hold the key.", e);
            }

            var held = ReadOrFail(slot);
            if (held is null || !held.AsSpan().SequenceEqual(envelope))
            {
                throw Failed("The storage does not hold what was written under the slot.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(scalar);
        }
    }

    /// <inheritdoc />
    public IPersonaSigner? OpenSigner(PersonaSlotId slot)
    {
        using var material = Open(slot);
        if (material is null)
        {
            return null;
        }

        try
        {
            return material.CreateSigner();
        }
        catch (PersonaException e)
        {
            Unavailable(slot, "the key could not be copied into a signer (" + e.Error + ")");
            return null;
        }
    }

    /// <inheritdoc />
    public PersonaKeyMaterial? OpenKey(PersonaSlotId slot) => Open(slot);

    /// <summary>
    /// The envelope decoded, unprotected and imported: the key the slot holds, or null with a
    /// reported reason. Every reason is unavailability, never an exception: no envelope, an
    /// envelope that does not decode or names another slot, a protector other than this store's,
    /// a blob the protector cannot open, a scalar that does not belong to the recorded public key,
    /// or a storage or protector that throws.
    /// </summary>
    private PersonaKeyMaterial? Open(PersonaSlotId slot)
    {
        if (slot.IsEmpty)
        {
            return Unavailable(slot, "the empty slot never holds a key");
        }

        byte[]? envelope;
        try
        {
            envelope = storage.Read(slot);
        }
        catch (Exception)
        {
            return Unavailable(slot, "the storage could not be read");
        }

        if (envelope is null)
        {
            return Unavailable(slot, "no key is held");
        }

        if (!ProtectedKeyEnvelope.TryDecode(envelope, out var decoded))
        {
            return Unavailable(slot, "what is held is not a key envelope this build reads");
        }

        if (decoded.Slot != slot)
        {
            return Unavailable(slot, "the envelope names another slot");
        }

        if (!string.Equals(decoded.ProtectorId, protector.Id, StringComparison.Ordinal))
        {
            return Unavailable(slot, "the key was protected by another protector");
        }

        byte[]? scalar;
        try
        {
            scalar = protector.Unprotect(decoded.Blob, decoded.Context);
        }
        catch (Exception)
        {
            return Unavailable(slot, "the protector failed");
        }

        if (scalar is null)
        {
            return Unavailable(slot, "the protector could not open the key on this account");
        }

        try
        {
            return PersonaKeyMaterial.Import(scalar, decoded.PublicKey);
        }
        catch (PersonaException e)
        {
            return Unavailable(slot, "the key does not belong to its recorded public key (" + e.Error + ")");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(scalar);
        }
    }

    /// <summary>Proves, before anything is durable, that <paramref name="envelope"/> opens to exactly the key being committed.</summary>
    private void ProveHoldsKey(byte[] envelope, PersonaSlotId slot, PersonaPublicKey publicKey, byte[] scalar)
    {
        if (!ProtectedKeyEnvelope.TryDecode(envelope, out var decoded) || decoded.Slot != slot || !decoded.PublicKey.Equals(publicKey))
        {
            throw Failed("The envelope written for the key does not decode to its slot and public key.");
        }

        byte[]? opened;
        try
        {
            opened = protector.Unprotect(decoded.Blob, decoded.Context);
        }
        catch (Exception e)
        {
            throw Failed("The protector could not open what it had just protected.", e);
        }

        if (opened is null)
        {
            throw Failed("The protector could not open what it had just protected.");
        }

        try
        {
            if (!CryptographicOperations.FixedTimeEquals(opened, scalar))
            {
                throw Failed("The protector opened a different scalar than it was given.");
            }

            using var check = PersonaKeyMaterial.Import(opened, decoded.PublicKey);
            if (!check.PublicKey.Equals(publicKey))
            {
                throw Failed("The envelope's key is not the key being committed.");
            }
        }
        catch (PersonaException e) when (e.Error != PersonaError.CustodyFailed)
        {
            throw Failed("The envelope's scalar could not be imported as the key being committed.", e);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(opened);
        }
    }

    private byte[]? ReadOrFail(PersonaSlotId slot)
    {
        try
        {
            return storage.Read(slot);
        }
        catch (Exception e)
        {
            throw Failed("The storage could not be read.", e);
        }
    }

    private PersonaKeyMaterial? Unavailable(PersonaSlotId slot, string reason)
    {
        report?.Invoke("The key under " + slot + " is unavailable: " + reason + ".");
        return null;
    }

    private static PersonaException Failed(string message) => new(PersonaError.CustodyFailed, message);

    private static PersonaException Failed(string message, Exception inner) => new(PersonaError.CustodyFailed, message, inner);
}
