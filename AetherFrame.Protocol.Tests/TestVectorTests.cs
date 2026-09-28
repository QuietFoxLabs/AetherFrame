using System;
using System.IO;
using System.Linq;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Remote;
using AetherFrame.Protocol.Signing;
using Xunit;

namespace AetherFrame.Protocol.Tests;

/// <summary>
/// The committed vectors (Fixtures/vectors-v1.json) hold what an independent implementation
/// needs: the personas, the canonical bytes, the digests, valid signatures, and documents that
/// must be refused. Set AETHERFRAME_PROTOCOL_REGENERATE_VECTORS=1 to rewrite them deliberately.
/// </summary>
public class TestVectorTests
{
    [Fact]
    public void Fixture_DescribesThisProtocolVersion()
    {
        var fixture = VectorFixture.Load();
        Assert.Equal(ProtocolConstants.ProtocolVersion, fixture.ProtocolVersion);
        Assert.Equal("AetherFrame.Protocol.SignedDocument.v1", fixture.SignatureDomainTag);
        Assert.Equal("AetherFrame.Protocol.PersonaId.v1", fixture.PersonaIdDomainTag);
        Assert.Equal(["A", "B"], fixture.Personas.Select(p => p.Name));
        Assert.Equal(VectorBuilder.Models().Select(m => m.Name), fixture.Documents.Select(d => d.Name));
        Assert.True(fixture.Rejected.Count >= 40);
    }

    [Fact]
    public void Personas_FollowFromTheirLabels()
    {
        foreach (var persona in VectorFixture.Load().Personas)
        {
            var scalar = TestPersonas.Scalar(persona.Label);
            Assert.Equal(Hex.Of(ReferenceP256.ToBytes32(scalar)), persona.PrivateScalar);
            var (x, y) = ReferenceP256.PublicKey(scalar);
            Assert.Equal("04" + Hex.Of(x) + Hex.Of(y), persona.PublicKey);
            Assert.Equal(persona.PersonaId, PersonaPublicKey.FromBytes(Hex.Parse(persona.PublicKey)).Id.ToString());
            using var signer = TestPersonas.Create(scalar);
            Assert.Equal(persona.PublicKey, Hex.Of(signer.PublicKey.Bytes));
        }
    }

    [Fact]
    public void Documents_VerifyWithTheLibraryAndTheReferenceImplementation()
    {
        var fixture = VectorFixture.Load();
        foreach (var vector in fixture.Documents)
        {
            var model = VectorBuilder.Models().Single(m => m.Name == vector.Name).Model;
            var key = PersonaPublicKey.FromBytes(Hex.Parse(fixture.Personas.Single(p => p.Name == vector.Persona).PublicKey));
            var payload = vector.Payload is null ? model.EncodePayload() : Hex.Parse(vector.Payload);
            var input = SigningInput.Create((DocumentType)vector.DocumentType, key, payload);
            if (vector.SigningInput is not null)
            {
                Assert.Equal(vector.SigningInput, Hex.Of(input.Bytes));
            }

            Assert.Equal(vector.Digest, Hex.Of(input.ComputeDigest()));
            var signature = ProtocolSignature.FromBytes(Hex.Parse(vector.Signature));
            Assert.True(SignatureVerifier.Verify(input, signature), vector.Name + " (library)");
            Assert.True(ReferenceP256.Verify(key.Bytes, input.ComputeDigest(), signature.Bytes), vector.Name + " (reference)");

            var document = SignedDocumentCodec.Assemble((DocumentType)vector.DocumentType, key, payload, signature);
            if (vector.Document is not null)
            {
                Assert.Equal(vector.Document, Hex.Of(document));
            }

            var verified = SignedDocumentCodec.Verify(document);
            Assert.Equal(key.Id, verified.Persona);
            Assert.Equal(payload, verified.Document.EncodePayload());
            if (vector.Snapshot is { } expected)
            {
                var snapshot = Assert.IsType<ProfileSnapshot>(verified.Document);
                var actual = VectorBuilder.Expected(snapshot);
                Assert.Equal(expected.ProfileId, actual.ProfileId);
                Assert.Equal(expected.RevisionId, actual.RevisionId);
                Assert.Equal(expected.CreatedAt, actual.CreatedAt);
                Assert.Equal(expected.Name, actual.Name);
                Assert.Equal(expected.Images.Select(i => (i.AssetId, i.Sha256, i.Format, i.ByteLength, i.Width, i.Height)), actual.Images.Select(i => (i.AssetId, i.Sha256, i.Format, i.ByteLength, i.Width, i.Height)));
            }

            if (vector.Retraction is { } retraction)
            {
                var actual = Assert.IsType<ProfileRetraction>(verified.Document);
                Assert.Equal(retraction.ProfileId, actual.ProfileId.ToString());
                Assert.Equal(retraction.IssuedAt, actual.IssuedAtUnixSeconds);
            }
        }
    }

    [Fact]
    public void Documents_DeterministicPartsMatchTheModels()
    {
        // Everything but the signature follows from the model and the key: a drift between the
        // fixture and the code shows here, whichever side moved.
        var fixture = VectorFixture.Load();
        foreach (var model in VectorBuilder.Models())
        {
            var vector = fixture.Documents.Single(d => d.Name == model.Name);
            var key = PersonaPublicKey.FromBytes(Hex.Parse(fixture.Personas.Single(p => p.Name == model.Persona).PublicKey));
            var payload = model.Model.EncodePayload();
            var input = SigningInput.Create(model.Model.DocumentType, key, payload);
            Assert.Equal(vector.Construction is null ? Hex.Of(payload) : null, vector.Payload);
            Assert.Equal(vector.Construction is null ? Hex.Of(input.Bytes) : null, vector.SigningInput);
            Assert.Equal(Hex.Of(input.ComputeDigest()), vector.Digest);
            Assert.Equal((byte)model.Model.DocumentType, vector.DocumentType);
        }
    }

    [Fact]
    public void RejectedDocuments_AreRefusedWithTheirError()
    {
        foreach (var vector in VectorFixture.Load().Rejected)
        {
            var expected = Enum.Parse<ProtocolError>(vector.Error);
            var document = Hex.Parse(vector.Document);
            var actual = ProtocolAssert.Rejects(() => SignedDocumentCodec.Verify(document)).Error;
            Assert.True(expected == actual, $"{vector.Name}: expected {expected}, got {actual}");
        }
    }

    [Fact]
    public void RejectedDocuments_DeterministicOnesFollowFromTheBaseDocument()
    {
        var fixture = VectorFixture.Load();
        using var signer = TestPersonas.CreateA();
        var otherKey = PersonaPublicKey.FromBytes(Hex.Parse(fixture.Personas.Single(p => p.Name == "B").PublicKey));
        var rebuilt = RejectedVectorBuilder.Build(Hex.Parse(fixture.Documents[0].Document!), signer, otherKey);
        Assert.Equal(rebuilt.Select(r => r.Name), fixture.Rejected.Select(r => r.Name));
        foreach (var (expected, actual) in rebuilt.Zip(fixture.Rejected))
        {
            Assert.Equal(expected.Error, actual.Error);
            Assert.Equal(expected.Deterministic, actual.Deterministic);
            if (expected.Deterministic)
            {
                Assert.True(expected.Document == actual.Document, expected.Name);
            }
        }
    }

    [Fact]
    public void Regenerate_WhenAskedTo()
    {
        if (Environment.GetEnvironmentVariable("AETHERFRAME_PROTOCOL_REGENERATE_VECTORS") is not { Length: > 0 })
        {
            return;
        }

        var fixtures = VectorPaths.SourceFixtures();
        Directory.CreateDirectory(fixtures);
        VectorBuilder.Build().Save(Path.Combine(fixtures, "vectors-v1.json"));
    }
}
