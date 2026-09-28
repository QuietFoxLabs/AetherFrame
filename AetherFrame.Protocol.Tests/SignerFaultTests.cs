using System;
using System.Security.Cryptography;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Remote;
using AetherFrame.Protocol.Signing;
using Xunit;

namespace AetherFrame.Protocol.Tests;

/// <summary>
/// An <see cref="IPersonaSigner"/> is outside the protocol's control (NETWORK1 will implement one
/// over a key store), so the codec does not trust it: the key is read once, and the document is
/// verified before it is handed out. A signer that misreports its key, signs with another key,
/// signs other bytes or returns nothing produces no document.
/// </summary>
public class SignerFaultTests
{
    [Fact]
    public void Sign_ReadsTheSignersPublicKeyOnce()
    {
        using var real = TestPersonas.CreateA();
        var reads = 0;
        var counting = new DelegatingSigner(() => { reads++; return real.PublicKey; }, real.Sign);

        var document = SignedDocumentCodec.Sign(Samples.Snapshot(), counting);

        Assert.Equal(1, reads);
        Assert.Equal(real.PublicKey.Id, SignedDocumentCodec.Verify(document).Persona);
    }

    [Fact]
    public void Sign_WithAnUnstableKeyReport_UsesTheKeyItReadForBothTheInputAndTheEnvelope()
    {
        using var real = TestPersonas.CreateA();
        using var other = TestPersonas.CreateB();
        var reads = 0;
        var unstable = new DelegatingSigner(() => reads++ == 0 ? real.PublicKey : other.PublicKey, real.Sign);

        var document = SignedDocumentCodec.Sign(Samples.Retraction(), unstable);

        Assert.Equal(real.PublicKey.Id, SignedDocumentCodec.Verify(document).Persona);
    }

    [Fact]
    public void Sign_RefusesASignerThatSignsWithAnotherKey()
    {
        using var reported = TestPersonas.CreateA();
        using var actual = TestPersonas.CreateEcdsa(TestPersonas.ScalarB);
        var impostor = new DelegatingSigner(() => reported.PublicKey, input => RawSign(actual, input));

        var error = ProtocolAssert.Throws(ProtocolError.SignatureMismatch, () => SignedDocumentCodec.Sign(Samples.Snapshot(), impostor));
        Assert.Contains("none was produced", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sign_RefusesASignatureOverOtherBytes()
    {
        using var real = TestPersonas.CreateA();
        var otherInput = SigningInput.Create(DocumentType.ProfileRetraction, real.PublicKey, Samples.Retraction().EncodePayload());
        var stale = new DelegatingSigner(() => real.PublicKey, _ => real.Sign(otherInput));

        ProtocolAssert.Throws(ProtocolError.SignatureMismatch, () => SignedDocumentCodec.Sign(Samples.Snapshot(), stale));
    }

    [Fact]
    public void Sign_RefusesASignerThatReturnsNothing()
    {
        using var real = TestPersonas.CreateA();
        var noSignature = new DelegatingSigner(() => real.PublicKey, _ => null!);
        ProtocolAssert.Throws(ProtocolError.InvalidSignature, () => SignedDocumentCodec.Sign(Samples.Snapshot(), noSignature));

        var noKey = new DelegatingSigner(() => null!, real.Sign);
        ProtocolAssert.Throws(ProtocolError.InvalidKey, () => SignedDocumentCodec.Sign(Samples.Snapshot(), noKey));
    }

    [Fact]
    public void Sign_LetsTheSignersOwnExceptionsThrough()
    {
        using var real = TestPersonas.CreateA();
        var failing = new DelegatingSigner(() => real.PublicKey, _ => throw new InvalidOperationException("key store locked"));
        var exception = Assert.Throws<InvalidOperationException>(() => SignedDocumentCodec.Sign(Samples.Snapshot(), failing));
        Assert.Equal("key store locked", exception.Message);
    }

    [Fact]
    public void Sign_HandsOutOnlyDocumentsTheReaderAccepts()
    {
        using var a = TestPersonas.CreateA();
        using var b = TestPersonas.CreateB();
        foreach (var (model, signer) in new (RemoteDocument, EcdsaPersonaSigner)[] { (Samples.Snapshot(), a), (Samples.Retraction(), b), (PayloadBuilder.MaximalSnapshot(), a) })
        {
            var verified = SignedDocumentCodec.Verify(SignedDocumentCodec.Sign(model, signer));
            Assert.Equal(signer.PublicKey.Id, verified.Persona);
            Assert.Equal(model.EncodePayload(), verified.Document.EncodePayload());
        }
    }

    /// <summary>Signs around <see cref="EcdsaPersonaSigner"/>, which would refuse an input naming another persona.</summary>
    private static ProtocolSignature RawSign(ECDsa key, SigningInput input)
    {
        var rs = key.SignData(input.Bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return ProtocolSignature.Normalize(rs);
    }

    private sealed class DelegatingSigner(Func<PersonaPublicKey> publicKey, Func<SigningInput, ProtocolSignature> sign) : IPersonaSigner
    {
        public PersonaPublicKey PublicKey => publicKey();

        public ProtocolSignature Sign(SigningInput input) => sign(input);
    }
}
