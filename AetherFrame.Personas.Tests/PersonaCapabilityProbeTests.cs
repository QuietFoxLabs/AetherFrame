using System;
using System.IO;
using System.Linq;
using AetherFrame.Services.Network.Personas;
using Xunit;

namespace AetherFrame.Personas.Tests;

/// <summary>
/// The capability probe (docs/networking/DecisionRegister.md, K3): viewing needs verification only;
/// persona features need the whole key chain and a protector that claims protection and binds its
/// blobs. Every failure turns capabilities off and nothing throws.
/// </summary>
public class PersonaCapabilityProbeTests : IDisposable
{
    private readonly TemporaryDirectory directory = new();

    public void Dispose() => directory.Dispose();

    private static bool Claims(byte[] blob) => true;

    private static bool ClaimsNothing(byte[] blob) => false;

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

        var refusing = PersonaCapabilityProbe.Run(new FakeProtector { FailNextProtect = new InvalidOperationException("no") }, Claims, directory.Path);
        Assert.Equal(PersonaCapability.KeyChain, refusing.Missing);

        // A scratch root that is a file: nothing can be stored under it.
        var file = Path.Combine(directory.Path, "not-a-directory");
        File.WriteAllBytes(file, [1]);
        var unwritable = PersonaCapabilityProbe.Run(new FakeProtector(), Claims, file);
        Assert.Equal(PersonaCapability.KeyChain, unwritable.Missing);
        Assert.Contains("available on this system yet", unwritable.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AVerificationFailure_TurnsEverythingOff()
    {
        var knownAnswer = Convert.FromHexString(PersonaCapabilityProbe.KnownAnswerHex);
        var brokenSignature = (byte[])knownAnswer.Clone();
        brokenSignature[^1] ^= 0x01;
        foreach (var answer in new[] { brokenSignature, knownAnswer[..^1], Array.Empty<byte>() })
        {
            var result = PersonaCapabilityProbe.Run(new FakeProtector(), Claims, directory.Path, answer);
            Assert.False(result.CanView);
            Assert.False(result.CanUsePersonas);
            Assert.Equal(PersonaCapability.SignatureVerification, result.Missing);
            Assert.Contains("Sharing isn't available on this system yet", result.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheKnownAnswer_VerifiesAsTheCommittedPersona()
    {
        var verified = AetherFrame.Protocol.Documents.SignedDocumentCodec.Verify(Convert.FromHexString(PersonaCapabilityProbe.KnownAnswerHex));
        Assert.Equal(PersonaCapabilityProbe.KnownAnswerPersona, verified.Persona.ToString());
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
        var protector = new DpapiPersonaKeyProtector();
        var result = PersonaCapabilityProbe.Run(protector, blob => DpapiPersonaKeyProtector.CarriesWindowsProvider(blob), directory.Path);
        Assert.True(result.CanView);
        Assert.Equal(OperatingSystem.IsWindows(), result.CanUsePersonas);
        Assert.Equal(OperatingSystem.IsWindows() ? PersonaCapability.None : PersonaCapability.KeyChain, result.Missing);
        Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
    }

    [Fact]
    public void Run_RefusesMissingArguments()
    {
        Assert.Throws<ArgumentNullException>(() => PersonaCapabilityProbe.Run(null!, Claims, directory.Path));
        Assert.Throws<ArgumentNullException>(() => PersonaCapabilityProbe.Run(new FakeProtector(), null!, directory.Path));
        Assert.Throws<ArgumentNullException>(() => PersonaCapabilityProbe.Run(new FakeProtector(), Claims, null!));
    }

    /// <summary>A protector whose blobs open under any context: it binds nothing.</summary>
    private sealed class ContextBlindProtector : AetherFrame.Personas.Storage.IPersonaKeyProtector
    {
        public string Id => "test.context-blind.v1";

        public byte[] Protect(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> context) => secret.ToArray().Select(b => (byte)(b ^ 0x5a)).ToArray();

        public byte[]? Unprotect(ReadOnlySpan<byte> blob, ReadOnlySpan<byte> context) => blob.ToArray().Select(b => (byte)(b ^ 0x5a)).ToArray();
    }
}
