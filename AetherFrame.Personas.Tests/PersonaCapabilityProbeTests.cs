using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using AetherFrame.Personas.Storage;
using AetherFrame.Protocol;
using AetherFrame.Protocol.Documents;
using AetherFrame.Services.Network.Personas;
using Xunit;

namespace AetherFrame.Personas.Tests;

/// <summary>
/// The capability probe (docs/networking/DecisionRegister.md, K3): viewing needs verification only,
/// and fails closed on every way verification could be wrong; persona features need the whole key
/// chain and a protector that claims protection and binds its blobs. No platform or storage failure
/// throws.
/// </summary>
public class PersonaCapabilityProbeTests : IDisposable
{
    /// <summary>Persona B's committed <c>profile-retraction</c> vector: a valid document of another persona.</summary>
    private const string OtherPersonaDocumentHex =
        "414650448001020438d018ac1a9716c8df48cd8a18e5d8e3ead537251ebcbf45bd03a97fe874ba48d377ea36e04cc10fda8c2af676901d789528af53245bd958fa4f256067f67e880000001a0001e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5000000006553f164147fb612ad7dbb00efa14dfb24887b8290fce532a8817235e5d65f88b757a13077c7eeb8032fca829a56572dac4192c489dd1aae4849afd3b6973245e3c43b9d";

    private readonly TemporaryDirectory directory = new();

    public void Dispose() => directory.Dispose();

    private static bool Claims(byte[] blob) => true;

    private static bool ClaimsNothing(byte[] blob) => false;

    private static byte[] KnownAnswer => Convert.FromHexString(PersonaCapabilityProbe.KnownAnswerHex);

    [Fact]
    public void AWorkingChainWithAClaimingProtector_TurnsEverythingOn()
    {
        var result = PersonaCapabilityProbe.Run(new FakeProtector(), Claims, directory.Path);
        Assert.True(result.CanView);
        Assert.True(result.CanUsePersonas);
        Assert.Equal(PersonaCapability.None, result.Missing);
        Assert.Null(result.Message);
        Assert.Null(result.Detail);
    }

    [Fact]
    public void AProtectorThatClaimsNoProtection_LeavesViewingOnly()
    {
        var result = PersonaCapabilityProbe.Run(new FakeProtector(), ClaimsNothing, directory.Path);
        Assert.True(result.CanView);
        Assert.False(result.CanUsePersonas);
        Assert.Equal(PersonaCapability.KeyProtection, result.Missing);
        Assert.Contains("available on this system yet", result.Message, StringComparison.Ordinal);
        Assert.Contains("Viewing shared Plates still works", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AProtectorThatOpensUnderAnyContext_IsNotTrusted()
    {
        var result = PersonaCapabilityProbe.Run(new ContextBlindProtector(), Claims, directory.Path);
        Assert.True(result.CanView);
        Assert.False(result.CanUsePersonas);
        Assert.Equal(PersonaCapability.KeyProtection, result.Missing);
        Assert.Contains("another context", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ABrokenChain_LeavesViewingOnly_AndNamesTheStep()
    {
        var locked = PersonaCapabilityProbe.Run(new FakeProtector { Locked = true }, Claims, directory.Path);
        Assert.Equal(PersonaCapability.KeyChain, locked.Missing);
        Assert.True(locked.CanView);
        Assert.False(locked.CanUsePersonas);
        Assert.Contains("storing the key", locked.Detail, StringComparison.Ordinal);

        // The store wraps a custody failure; the detail names the cause's kind too.
        var refusing = PersonaCapabilityProbe.Run(new FakeProtector { FailNextProtect = new InvalidOperationException("no") }, Claims, directory.Path);
        Assert.Equal(PersonaCapability.KeyChain, refusing.Missing);
        Assert.Contains("PersonaException from InvalidOperationException", refusing.Detail, StringComparison.Ordinal);

        // A scratch root that is a file: nothing can be stored under it.
        var file = Path.Combine(directory.Path, "not-a-directory");
        File.WriteAllBytes(file, [1]);
        var unwritable = PersonaCapabilityProbe.Run(new FakeProtector(), Claims, file);
        Assert.Equal(PersonaCapability.KeyChain, unwritable.Missing);
        Assert.Contains("available on this system yet", unwritable.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AKeyThatDoesNotOpenAgain_IsAChainFailure_AndItsFileIsDeleted()
    {
        // The key is written, then refused when opened again: cleanup must remove what was written.
        var result = PersonaCapabilityProbe.Run(new OpensOnceProtector(), Claims, directory.Path);
        Assert.Equal(PersonaCapability.KeyChain, result.Missing);
        Assert.Contains("opened again", result.Detail, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
    }

    [Fact]
    public void AVerificationFailure_TurnsEverythingOff_BeforeTheChainIsTried()
    {
        var brokenSignature = KnownAnswer;
        brokenSignature[^1] ^= 0x01;
        foreach (var answer in new[] { brokenSignature, KnownAnswer[..^1], Array.Empty<byte>() })
        {
            // A locked protector as well: were the chain tried first, its failure would leave viewing on.
            var result = PersonaCapabilityProbe.Run(new FakeProtector { Locked = true }, Claims, directory.Path, answer);
            Assert.False(result.CanView);
            Assert.False(result.CanUsePersonas);
            Assert.Equal(PersonaCapability.SignatureVerification, result.Missing);
            Assert.Contains("Sharing isn't available on this system yet", result.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AKnownAnswerOfAnotherPersona_TurnsEverythingOff()
    {
        var result = PersonaCapabilityProbe.Run(new FakeProtector(), Claims, directory.Path, Convert.FromHexString(OtherPersonaDocumentHex));
        Assert.Equal(PersonaCapability.SignatureVerification, result.Missing);
        Assert.False(result.CanView);
        Assert.Contains("another persona", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AVerifierThatAcceptsTheTamperedCopy_OrRefusesItForAnotherReason_TurnsEverythingOff()
    {
        // The platform's verifier always refuses a changed payload bit as a signature mismatch, so
        // these two failures are shown through the probe's verifier seam.
        var genuine = SignedDocumentCodec.Verify(KnownAnswer);
        var acceptsAnything = PersonaCapabilityProbe.Run(new FakeProtector(), Claims, directory.Path, verify: _ => genuine);
        Assert.Equal(PersonaCapability.SignatureVerification, acceptsAnything.Missing);
        Assert.False(acceptsAnything.CanView);
        Assert.Contains("tampered copy of the known-answer document verified", acceptsAnything.Detail, StringComparison.Ordinal);

        var original = KnownAnswer;
        var wrongReason = PersonaCapabilityProbe.Run(new FakeProtector(), Claims, directory.Path, verify: bytes =>
            bytes.AsSpan().SequenceEqual(original) ? genuine : throw new ProtocolException(ProtocolError.InvalidValue, "Refused, but not for its signature."));
        Assert.Equal(PersonaCapability.SignatureVerification, wrongReason.Missing);
        Assert.False(wrongReason.CanView);
        Assert.Contains("wrong reason", wrongReason.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void TheKnownAnswer_IsTheCommittedVector_AndVerifiesAsItsPersona()
    {
        var verified = SignedDocumentCodec.Verify(KnownAnswer);
        Assert.Equal(PersonaCapabilityProbe.KnownAnswerPersona, verified.Persona.ToString());

        var fixture = Path.Combine(RepositoryRoot(), "AetherFrame.Protocol.Tests", "Fixtures", "vectors-v1.json");
        using var json = JsonDocument.Parse(File.ReadAllBytes(fixture));
        var committed = json.RootElement.GetProperty("documents").EnumerateArray().Single(d => d.GetProperty("name").GetString() == "profile-snapshot");
        Assert.Equal(PersonaCapabilityProbe.KnownAnswerHex, committed.GetProperty("document").GetString());
    }

    [Fact]
    public void TheProbe_LeavesNothingBehind()
    {
        PersonaCapabilityProbe.Run(new FakeProtector(), Claims, directory.Path);
        PersonaCapabilityProbe.Run(new FakeProtector { Locked = true }, Claims, directory.Path);
        Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
    }

    [Fact]
    public void WithDpapi_PersonasTurnOnExactlyWhereDpapiProtects()
    {
        var result = PersonaCapabilityProbe.Run(new DpapiPersonaKeyProtector(), directory.Path);
        Assert.True(result.CanView);
        Assert.Equal(OperatingSystem.IsWindows(), result.CanUsePersonas);
        Assert.Equal(OperatingSystem.IsWindows() ? PersonaCapability.None : PersonaCapability.KeyChain, result.Missing);
        Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
    }

    [Fact]
    public void Run_RefusesMissingArgumentsAndARelativeScratchRoot()
    {
        Assert.Throws<ArgumentNullException>(() => PersonaCapabilityProbe.Run(null!, Claims, directory.Path));
        Assert.Throws<ArgumentNullException>(() => PersonaCapabilityProbe.Run(new FakeProtector(), null!, directory.Path));
        Assert.Throws<ArgumentNullException>(() => PersonaCapabilityProbe.Run(new FakeProtector(), Claims, null!));
        Assert.Throws<ArgumentNullException>(() => PersonaCapabilityProbe.Run((DpapiPersonaKeyProtector)null!, directory.Path));
        Assert.Throws<ArgumentException>(() => PersonaCapabilityProbe.Run(new FakeProtector(), Claims, "relative"));
        Assert.Throws<ArgumentException>(() => PersonaCapabilityProbe.Run(new DpapiPersonaKeyProtector(), Path.Combine("scratch", "root")));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AetherFrame.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    /// <summary>A protector whose blobs open under any context: it binds nothing.</summary>
    private sealed class ContextBlindProtector : IPersonaKeyProtector
    {
        public string Id => "test.context-blind.v1";

        public byte[] Protect(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> context) => secret.ToArray().Select(b => (byte)(b ^ 0x5a)).ToArray();

        public byte[]? Unprotect(ReadOnlySpan<byte> blob, ReadOnlySpan<byte> context) => blob.ToArray().Select(b => (byte)(b ^ 0x5a)).ToArray();
    }

    /// <summary>A protector that opens a blob once, when the store proves the key before writing it, and never again.</summary>
    private sealed class OpensOnceProtector : IPersonaKeyProtector
    {
        private readonly FakeProtector inner = new();
        private int opened;

        public string Id => inner.Id;

        public byte[] Protect(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> context) => inner.Protect(secret, context);

        public byte[]? Unprotect(ReadOnlySpan<byte> blob, ReadOnlySpan<byte> context) =>
            opened++ == 0 ? inner.Unprotect(blob, context) : null;
    }
}
