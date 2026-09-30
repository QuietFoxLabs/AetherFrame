using System;
using System.IO;
using System.Security.Cryptography;
using AetherFrame.Personas;
using AetherFrame.Personas.Storage;
using AetherFrame.Protocol;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Remote;

namespace AetherFrame.Services.Network.Personas;

/// <summary>What the capability probe found missing, in the order it checks.</summary>
public enum PersonaCapability
{
    /// <summary>Nothing: every capability is present.</summary>
    None = 0,

    /// <summary>Signatures cannot be verified here, so neither viewing nor personas turn on.</summary>
    SignatureVerification = 1,

    /// <summary>A key cannot be created, stored, opened and used here.</summary>
    KeyChain = 2,

    /// <summary>The key protector gives no platform protection here, or does not bind its blobs to their context.</summary>
    KeyProtection = 3,
}

/// <summary>
/// What a session may turn on (docs/networking/DecisionRegister.md, K3): viewing needs signature
/// verification only; persona features need the whole key chain and a protector that claims
/// protection. Every local feature stays on whatever this says.
/// </summary>
public sealed class PersonaCapabilities
{
    private PersonaCapabilities(bool canView, bool canUsePersonas, PersonaCapability missing, string? message, string? detail)
    {
        CanView = canView;
        CanUsePersonas = canUsePersonas;
        Missing = missing;
        Message = message;
        Detail = detail;
    }

    /// <summary>Whether a shared Plate can be viewed: signature verification works.</summary>
    public bool CanView { get; }

    /// <summary>Whether persona features can turn on: creating, opening or using a key, and publishing.</summary>
    public bool CanUsePersonas { get; }

    /// <summary>The first capability the probe found missing, or <see cref="PersonaCapability.None"/>.</summary>
    public PersonaCapability Missing { get; }

    /// <summary>
    /// The one message for the player when something is missing: it names the missing capability,
    /// says the feature isn't available on this system yet, and never says a system is excluded.
    /// Null when nothing is missing.
    /// </summary>
    public string? Message { get; }

    /// <summary>For the log: the step that failed and the kind of failure, never a key, a blob or a path.</summary>
    public string? Detail { get; }

    internal static PersonaCapabilities All { get; } = new(true, true, PersonaCapability.None, null, null);

    internal static PersonaCapabilities Without(PersonaCapability missing, string detail) => missing switch
    {
        PersonaCapability.SignatureVerification => new(false, false, missing, "Sharing isn't available on this system yet: AetherFrame couldn't check signatures here.", detail),
        PersonaCapability.KeyChain => new(true, false, missing, "Personas aren't available on this system yet: AetherFrame couldn't create and use a key here. Viewing shared Plates still works.", detail),
        PersonaCapability.KeyProtection => new(true, false, missing, "Personas aren't available on this system yet: AetherFrame can't protect keys with this system's key storage. Viewing shared Plates still works.", detail),
        _ => throw new ArgumentOutOfRangeException(nameof(missing)),
    };
}

/// <summary>
/// The capability probe (docs/networking/DecisionRegister.md, K3): run once per session, off the
/// framework thread, before any persona feature turns on. It decides by what works, never by the
/// operating system's name. It checks, in order:
/// <list type="number">
/// <item>that a committed known-answer document verifies and a tampered copy of it is refused as a
/// signature mismatch, since a round trip alone doesn't show that verification works;</item>
/// <item>the exact key chain the plugin uses, with a throwaway key and never a persona's: generate,
/// protect under the envelope's header, write and read the key file storage in a scratch directory,
/// open again through the key material's checks, sign, and verify through the protocol;</item>
/// <item>that the protector claims protection for a blob it has just made, opens that blob, and
/// refuses it under another context.</item>
/// </list>
/// It never throws: any failure turns the capabilities off. It deletes its scratch directory
/// whatever happens, and it never touches a persona's key or the plugin's own key directory.
/// </summary>
public static class PersonaCapabilityProbe
{
    /// <summary>
    /// Persona A's committed <c>profile-snapshot</c> test vector
    /// (AetherFrame.Protocol.Tests/Fixtures/vectors-v1.json): a draft document signed by a synthetic
    /// test key that exists for nothing else.
    /// </summary>
    internal const string KnownAnswerHex =
        "414650448001010465bc2391c653055c253c4090935d989757e36f091aa623026409ed18183b4215015735a96c1401fa5c912fd03e45627aa38558e3c3e9829e63e014bb36c5b91f000000c00001a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2000000006553f1000000000c53616d706c6520506c61746500000002c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c311111111111111111111111111111111111111111111111111111111111111110100000000000004d200000280000001e0d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4222222222222222222222222222222222222222222222222222222222222222202000000000000162e00000780000004383793f5e9e31a01a9ed5c3dbd764672a1f7e3a072d144436292915ec45a99a4c81c6d573a6ca6153ea21cf5a7529c4c58628b197576d8a7e0382e4c27f2ea4a16";

    /// <summary>Where a signed document's payload starts: magic, version, type, key and payload length come first.</summary>
    private const int KnownAnswerPayloadOffset = 4 + 2 + 1 + ProtocolConstants.PublicKeyLength + 4;

    /// <summary>The persona the known-answer document verifies as.</summary>
    internal const string KnownAnswerPersona = "psn_7d73559d8dd350e595d7a8b22481f9b74b7fe7399348f6a515efc655971677f5";

    /// <summary>
    /// Runs the probe with <paramref name="protector"/>, whose protection is judged by
    /// <paramref name="claimsProtection"/> on a blob it has just made (for DPAPI,
    /// <see cref="DpapiPersonaKeyProtector.CarriesWindowsProvider"/>), in a fresh directory under
    /// <paramref name="scratchRoot"/>. Blocking: call it off the framework thread.
    /// </summary>
    public static PersonaCapabilities Run(IPersonaKeyProtector protector, Func<byte[], bool> claimsProtection, string scratchRoot) =>
        Run(protector, claimsProtection, scratchRoot, Convert.FromHexString(KnownAnswerHex));

    /// <summary>The probe with another known answer, so the tests can show a verification failure turns everything off.</summary>
    internal static PersonaCapabilities Run(IPersonaKeyProtector protector, Func<byte[], bool> claimsProtection, string scratchRoot, byte[] knownAnswer)
    {
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(claimsProtection);
        ArgumentNullException.ThrowIfNull(scratchRoot);
        ArgumentNullException.ThrowIfNull(knownAnswer);

        if (VerificationFails(knownAnswer) is { } verification)
        {
            return PersonaCapabilities.Without(PersonaCapability.SignatureVerification, verification);
        }

        if (KeyChainFails(protector, scratchRoot) is { } chain)
        {
            return PersonaCapabilities.Without(PersonaCapability.KeyChain, chain);
        }

        if (ProtectionFails(protector, claimsProtection) is { } protection)
        {
            return PersonaCapabilities.Without(PersonaCapability.KeyProtection, protection);
        }

        return PersonaCapabilities.All;
    }

    private static string? VerificationFails(byte[] knownAnswer)
    {
        try
        {
            if (SignedDocumentCodec.Verify(knownAnswer).Persona.ToString() != KnownAnswerPersona)
            {
                return "the known-answer document verified as another persona";
            }
        }
        catch (Exception e)
        {
            return "the known-answer document did not verify (" + e.GetType().Name + ")";
        }

        // One bit of the payload changed: a verifier that accepts this verifies nothing.
        var tampered = (byte[])knownAnswer.Clone();
        tampered[KnownAnswerPayloadOffset] ^= 0x01;
        try
        {
            SignedDocumentCodec.Verify(tampered);
            return "a tampered copy of the known-answer document verified";
        }
        catch (ProtocolException e) when (e.Error == ProtocolError.SignatureMismatch)
        {
            return null;
        }
        catch (Exception e)
        {
            return "a tampered copy of the known-answer document was refused for the wrong reason (" + e.GetType().Name + ")";
        }
    }

    private static string? KeyChainFails(IPersonaKeyProtector protector, string scratchRoot)
    {
        var step = "preparing the scratch directory";
        string? directory = null;
        try
        {
            directory = Path.Combine(Path.GetFullPath(scratchRoot), "aetherframe-probe-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)));
            var store = new ProtectedPersonaKeyStore(new PersonaKeyFileStorage(directory), protector);

            step = "generating a key";
            using var material = store.GenerateKey();

            step = "protecting and storing the key";
            var slot = PersonaSlotId.NewId();
            store.AddKey(slot, material);

            step = "opening the stored key";
            var signer = store.OpenSigner(slot);
            if (signer is null)
            {
                return "the stored key could not be opened again";
            }

            try
            {
                step = "signing and verifying";
                var document = SignedDocumentCodec.Sign(new ProfileRetraction(ProfileId.NewId(), 1_700_000_000), signer);
                if (!SignedDocumentCodec.Verify(document).PublicKey.Equals(material.PublicKey))
                {
                    return "a document signed with the stored key verified as another key";
                }
            }
            finally
            {
                (signer as IDisposable)?.Dispose();
            }

            return null;
        }
        catch (Exception e)
        {
            return step + " failed (" + e.GetType().Name + ")";
        }
        finally
        {
            DeleteQuietly(directory);
        }
    }

    private static string? ProtectionFails(IPersonaKeyProtector protector, Func<byte[], bool> claimsProtection)
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var context = RandomNumberGenerator.GetBytes(32);
        byte[]? opened = null;
        byte[]? elsewhere = null;
        try
        {
            var blob = protector.Protect(secret, context);
            if (!claimsProtection(blob))
            {
                return "the protector's blobs carry no platform protection";
            }

            opened = protector.Unprotect(blob, context);
            if (opened is null || !CryptographicOperations.FixedTimeEquals(opened, secret))
            {
                return "the protector does not open its own blob";
            }

            var otherContext = (byte[])context.Clone();
            otherContext[0] ^= 0x01;
            elsewhere = protector.Unprotect(blob, otherContext);
            return elsewhere is null ? null : "the protector opens a blob under another context";
        }
        catch (Exception e)
        {
            return "the protector failed (" + e.GetType().Name + ")";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            if (opened is not null)
            {
                CryptographicOperations.ZeroMemory(opened);
            }

            if (elsewhere is not null)
            {
                CryptographicOperations.ZeroMemory(elsewhere);
            }
        }
    }

    private static void DeleteQuietly(string? directory)
    {
        if (directory is null)
        {
            return;
        }

        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
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
