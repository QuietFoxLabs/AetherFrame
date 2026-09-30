using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Remote;
using AetherFrame.Protocol.Requests;
using AetherFrame.Protocol.Signing;
using Xunit;

namespace AetherFrame.Protocol.Tests;

/// <summary>
/// The committed vectors (Fixtures/vectors-v1.json) hold what an independent implementation
/// needs: the personas, the canonical bytes, the digests, valid signatures, documents that must be
/// refused, and valid documents a server must apply in a particular way. Set
/// AETHERFRAME_PROTOCOL_REGENERATE_VECTORS=1 to rewrite them deliberately (the approved public API
/// list has its own switch, in AssemblyBoundaryTests).
/// </summary>
public class TestVectorTests
{
    [Fact]
    public void FinalMarkerVectors_AreSignedOverTheFinalMarker_AndNeverVerifyAsDrafts()
    {
        // Decision N3: the two final-marker vectors carry a genuine signature over the final signing
        // input (version 1, the tag without "-draft"), so what makes a draft reader refuse them is the
        // marker alone, never a broken signature.
        var fixture = VectorFixture.Load();
        var final = Hex.Parse(fixture.Rejected.Single(r => r.Name == "final-version-1-document").Document);
        var relabelled = Hex.Parse(fixture.Rejected.Single(r => r.Name == "final-signature-under-draft-version").Document);
        Assert.Equal(new byte[] { 0x00, 0x01 }, final.AsSpan(4, 2).ToArray());
        Assert.Equal(new byte[] { 0x80, 0x01 }, relabelled.AsSpan(4, 2).ToArray());

        var key = final.AsSpan(7, 65).ToArray();
        var payload = final.AsSpan(Layout.Payload, Layout.PayloadLengthOf(final)).ToArray();
        var signature = final.AsSpan(final.Length - 64, 64).ToArray();
        var finalDigest = System.Security.Cryptography.SHA256.HashData(ReferenceProtocol.SigningInput(ReferenceProtocol.Final, final[6], key, payload));
        var draftDigest = System.Security.Cryptography.SHA256.HashData(ReferenceProtocol.SigningInput(ReferenceProtocol.Draft, final[6], key, payload));
        Assert.True(ReferenceP256.Verify(key, finalDigest, signature));
        Assert.False(ReferenceP256.Verify(key, draftDigest, signature));
        Assert.Equal(signature, relabelled.AsSpan(relabelled.Length - 64, 64).ToArray());

        ProtocolAssert.Throws(ProtocolError.UnsupportedVersion, () => SignedDocumentCodec.Verify(final));
        ProtocolAssert.Throws(ProtocolError.SignatureMismatch, () => SignedDocumentCodec.Verify(relabelled));
    }

    [Fact]
    public void Fixture_DescribesThisProtocolVersion()
    {
        var fixture = VectorFixture.Load();
        Assert.Equal(ProtocolConstants.ProtocolVersion, fixture.ProtocolVersion);
        Assert.Equal(0x8001, fixture.ProtocolVersion);
        Assert.Equal("AetherFrame.Protocol.SignedDocument.v1-draft", fixture.SignatureDomainTag);
        Assert.Equal("AetherFrame.Protocol.PersonaId.v1", fixture.PersonaIdDomainTag);
        Assert.Equal("AetherFrame.Protocol.RequestProof.v1-draft", fixture.RequestProofDomainTag);
        Assert.Equal(RequestProofVectorBuilder.Valid().Select(p => p.Name), fixture.RequestProofs.Select(p => p.Name));
        Assert.True(fixture.RejectedProofs.Count >= 43);
        Assert.Equal(["A", "B"], fixture.Personas.Select(p => p.Name));
        Assert.Equal(VectorBuilder.Models().Select(m => m.Name), fixture.Documents.Select(d => d.Name));
        Assert.True(fixture.Rejected.Count >= 40);
        Assert.Equal(["retraction-by-another-persona", "snapshot-by-another-persona"], fixture.ServerObligations.Select(o => o.Name));
        Assert.StartsWith("DRAFT", fixture.Notes, StringComparison.Ordinal);
        Assert.Equal(Samples.ProfileOwners.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (p.Key, p.Value)), fixture.Profiles.Select(p => (p.ProfileId, p.Owner)));
    }

    [Fact]
    public void ValidDocuments_AreSignedByTheRecordedOwnerOfTheirProfile()
    {
        // The documents section holds the valid examples: each is signed by the persona the fixture's
        // owner table records for its profile id, and its verified profile is that owner's. A document
        // that made another persona appear to publish to, or retract, a recorded owner's profile
        // cannot be listed here; it belongs under serverObligations, with the owner it is not.
        var fixture = VectorFixture.Load();
        var owners = fixture.Profiles.ToDictionary(p => p.ProfileId, p => p.Owner, StringComparer.Ordinal);
        var personas = fixture.Personas.ToDictionary(p => p.Name, p => PersonaId.Parse(p.PersonaId), StringComparer.Ordinal);
        foreach (var vector in fixture.Documents)
        {
            var model = Assert.IsAssignableFrom<RemoteProfileDocument>(VectorBuilder.Models().Single(m => m.Name == vector.Name).Model);
            var profileId = model.ProfileId.ToString();
            Assert.Equal(profileId, vector.Snapshot?.ProfileId ?? vector.Retraction?.ProfileId ?? profileId);
            Assert.True(owners.TryGetValue(profileId, out var owner), $"{vector.Name}: {profileId} has no recorded owner");
            Assert.True(owner == vector.Persona, $"{vector.Name}: signed by {vector.Persona}, but {profileId} belongs to {owner}");

            var document = vector.Document is null ? SignedDocumentCodec.Sign(model, TestPersonas.Create(TestPersonas.Scalar(fixture.Personas.Single(p => p.Name == vector.Persona).Label))) : Hex.Parse(vector.Document);
            var verified = SignedDocumentCodec.Verify(document);
            Assert.Equal(new RemoteProfileKey(personas[owner], model.ProfileId), ProtocolAssert.ProfileOf(verified));
        }
    }

    [Fact]
    public void ServerObligationDocuments_AreValidSignaturesButNotTheRecordedOwnersActs()
    {
        // Signature validity and authorization are different things: each obligation document is a
        // valid document of its signer about the signer's own profile of that id, and the fixture's
        // owner table says the id belongs to someone else. The protocol proves the first; only a
        // backend holding the owner table can refuse to treat it as the owner's act.
        var fixture = VectorFixture.Load();
        var owners = fixture.Profiles.ToDictionary(p => p.ProfileId, p => p.Owner, StringComparer.Ordinal);
        var personas = fixture.Personas.ToDictionary(p => p.Name, p => PersonaId.Parse(p.PersonaId), StringComparer.Ordinal);
        foreach (var vector in fixture.ServerObligations)
        {
            var verified = SignedDocumentCodec.Verify(Hex.Parse(vector.Document));
            Assert.Equal(owners[vector.ProfileId], vector.Owner);
            Assert.NotEqual(vector.Owner, vector.Persona);
            Assert.Equal(personas[vector.Persona], verified.Persona);
            Assert.Equal(new RemoteProfileKey(personas[vector.Persona], ProfileId.Parse(vector.ProfileId)), ProtocolAssert.ProfileOf(verified));
            Assert.NotEqual(new RemoteProfileKey(personas[vector.Owner], ProfileId.Parse(vector.ProfileId)), ProtocolAssert.ProfileOf(verified));
            Assert.DoesNotContain(fixture.Documents, d => d.Document == vector.Document);
        }
    }

    [Fact]
    public void SnapshotVectors_ShareARevisionIdOnlyWithIdenticalBytes()
    {
        // Within one profile a revision id names exactly one document (specification, section 13,
        // rule 4): valid vectors with different payloads carry different revision ids.
        var fixture = VectorFixture.Load();
        var snapshots = VectorBuilder.Models().Where(m => m.Model is ProfileSnapshot).Select(m => (m.Name, m.Persona, Snapshot: (ProfileSnapshot)m.Model)).ToList();
        Assert.Equal(4, snapshots.Count);
        foreach (var group in snapshots.GroupBy(s => (s.Persona, s.Snapshot.ProfileId, s.Snapshot.RevisionId)))
        {
            var payloads = group.Select(s => Hex.Of(s.Snapshot.EncodePayload())).Distinct().Count();
            Assert.True(payloads == 1, $"revision {group.Key.RevisionId} of ({group.Key.Persona}, {group.Key.ProfileId}) names {payloads} different documents: {string.Join(", ", group.Select(s => s.Name))}");
        }

        Assert.Equal(4, snapshots.Select(s => s.Snapshot.RevisionId).Distinct().Count());
        foreach (var vector in fixture.Documents.Where(d => d.Snapshot is not null))
        {
            Assert.Equal(snapshots.Single(s => s.Name == vector.Name).Snapshot.RevisionId.ToString(), vector.Snapshot!.RevisionId);
        }
    }

    [Fact]
    public void ServerObligationDocuments_AreAboutTheSigningPersonasProfile_NotTheOtherPersonas()
    {
        var fixture = VectorFixture.Load();
        var ownerA = PersonaId.Parse(fixture.Personas.Single(p => p.Name == "A").PersonaId);
        foreach (var vector in fixture.ServerObligations)
        {
            var verified = SignedDocumentCodec.Verify(Hex.Parse(vector.Document));
            var signer = PersonaId.Parse(fixture.Personas.Single(p => p.Name == vector.Persona).PersonaId);
            var profileId = ProfileId.Parse(vector.ProfileId);
            Assert.Equal(signer, verified.Persona);
            Assert.Equal(new RemoteProfileKey(signer, profileId), ProtocolAssert.ProfileOf(verified));
            Assert.NotEqual(new RemoteProfileKey(ownerA, profileId), ProtocolAssert.ProfileOf(verified));
            Assert.NotEqual(ownerA, verified.Persona);
        }
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
            Assert.Equal(persona.PersonaId, ReferenceProtocol.PersonaId(Hex.Parse(persona.PublicKey)));
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

            // The reference builds the signing input and the digest from the specification's tables;
            // the library must produce the same bytes, and the vector must hold them.
            var referenceInput = ReferenceProtocol.SigningInput((byte)vector.DocumentType, key.Bytes, payload);
            var referenceDigest = SHA256.HashData(referenceInput);
            Assert.Equal(referenceInput, input.Bytes.ToArray());
            if (vector.SigningInput is not null)
            {
                Assert.Equal(vector.SigningInput, Hex.Of(referenceInput));
            }

            Assert.Equal(vector.Digest, Hex.Of(referenceDigest));
            Assert.Equal(referenceDigest, input.ComputeDigest());
            var signature = ProtocolSignature.FromBytes(Hex.Parse(vector.Signature));
            Assert.True(SignatureVerifier.Verify(input, signature), vector.Name + " (library)");
            Assert.True(ReferenceP256.Verify(key.Bytes, referenceDigest, signature.Bytes), vector.Name + " (reference)");

            var document = SignedDocumentCodec.Assemble((DocumentType)vector.DocumentType, key, payload, signature);
            Assert.Equal(ReferenceProtocol.Document((byte)vector.DocumentType, key.Bytes, payload, signature.Bytes), document);
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
        var retraction = Hex.Parse(fixture.Documents.Single(d => d.Name == "profile-retraction").Document!);
        var rebuilt = RejectedVectorBuilder.Build(Hex.Parse(fixture.Documents[0].Document!), retraction, signer, otherKey);
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
    public void RequestProofs_VerifyWithTheLibraryAndTheReferenceImplementation()
    {
        var fixture = VectorFixture.Load();
        foreach (var vector in fixture.RequestProofs)
        {
            var key = PersonaPublicKey.FromBytes(Hex.Parse(fixture.Personas.Single(p => p.Name == vector.Persona).PublicKey));
            var document = Hex.Parse(fixture.Documents.Single(d => d.Name == vector.Document).Document!);
            var deployment = DeploymentName.Parse(vector.Deployment);
            var challenge = RequestChallenge.Parse(vector.Challenge);
            var subject = SHA256.HashData(document);
            Assert.Equal(vector.SubjectDigest, Hex.Of(subject));

            // The reference builds the signing input and the proof from the specification's tables;
            // the library must produce the same bytes, and the vector must hold them.
            var referenceInput = ReferenceProtocol.ProofSigningInput(1, key.Bytes, deployment.Bytes, challenge.Bytes, subject);
            var input = SigningInput.CreateRequestProof(RequestProofKind.DocumentSubmission, key, deployment, challenge, subject);
            Assert.Equal(referenceInput, input.Bytes.ToArray());
            Assert.Equal(vector.SigningInput, Hex.Of(referenceInput));
            Assert.Equal(vector.Digest, Hex.Of(SHA256.HashData(referenceInput)));
            Assert.True(ReferenceP256.Verify(key.Bytes, SHA256.HashData(referenceInput), Hex.Parse(vector.Signature)), vector.Name + " (reference)");
            Assert.True(SignatureVerifier.Verify(input, ProtocolSignature.FromBytes(Hex.Parse(vector.Signature))), vector.Name + " (library)");
            Assert.Equal(vector.Proof, Hex.Of(ReferenceProtocol.Proof(1, key.Bytes, deployment.Bytes, challenge.Bytes, subject, Hex.Parse(vector.Signature))));

            var submission = RequestProofCodec.VerifySubmission(Hex.Parse(vector.Proof), document, deployment);
            Assert.Equal(key.Id, submission.Proof.Persona);
            Assert.Equal(key.Id, submission.Document.Persona);
            Assert.Equal(challenge, submission.Challenge);
        }
    }

    [Fact]
    public void RejectedProofs_AreRefusedWithTheirError()
    {
        var fixture = VectorFixture.Load();
        foreach (var vector in fixture.RejectedProofs)
        {
            var expected = Enum.Parse<ProtocolError>(vector.Error);
            var document = vector.DocumentSet switch
            {
                null => Hex.Parse(fixture.Documents.Single(d => d.Name == vector.Document).Document!),
                "rejected" => Hex.Parse(fixture.Rejected.Single(r => r.Name == vector.Document).Document),
                _ => throw new InvalidDataException(vector.Name + ": unknown document set " + vector.DocumentSet),
            };
            var actual = ProtocolAssert.Rejects(() => RequestProofCodec.VerifySubmission(Hex.Parse(vector.Proof), document, DeploymentName.Parse(vector.Deployment))).Error;
            Assert.True(expected == actual, $"{vector.Name}: expected {expected}, got {actual}");
        }
    }

    [Fact]
    public void RejectedProofs_DeterministicOnesFollowFromTheBaseProof()
    {
        var fixture = VectorFixture.Load();
        using var a = TestPersonas.CreateA();
        using var b = TestPersonas.CreateB();
        byte[] DocumentNamed(string name) => Hex.Parse(fixture.Documents.Single(d => d.Name == name).Document!);
        byte[] RejectedNamed(string name) => Hex.Parse(fixture.Rejected.Single(r => r.Name == name).Document);
        var rebuilt = RequestProofVectorBuilder.BuildRejected(Hex.Parse(fixture.RequestProofs[0].Proof), DocumentNamed, RejectedNamed, a, b);
        Assert.Equal(rebuilt.Select(r => r.Name), fixture.RejectedProofs.Select(r => r.Name));
        foreach (var (expected, actual) in rebuilt.Zip(fixture.RejectedProofs))
        {
            Assert.Equal(expected.Error, actual.Error);
            Assert.Equal(expected.Document, actual.Document);
            Assert.Equal(expected.DocumentSet, actual.DocumentSet);
            Assert.Equal(expected.Deployment, actual.Deployment);
            Assert.Equal(expected.Deterministic, actual.Deterministic);
            if (expected.Deterministic)
            {
                Assert.True(expected.Proof == actual.Proof, expected.Name);
            }
        }
    }

    [Fact]
    public void FreshlySignedProofVectors_HoldWhatTheirReasonsSay()
    {
        // Both expect SignatureMismatch, which any broken proof also produces at section 14.3, step
        // 10. So what makes each one test its own rule is pinned here: a regeneration that broke
        // either would otherwise still pass as a refusal.
        var fixture = VectorFixture.Load();
        var keyA = PersonaPublicKey.FromBytes(Hex.Parse(fixture.Personas.Single(p => p.Name == "A").PublicKey));

        // The step-4 vector: a valid proof on its own, binding exactly the rejected document, whose
        // own signature is what fails.
        var step4 = fixture.RejectedProofs.Single(r => r.Name == "proof-of-a-document-that-does-not-verify");
        var verified = RequestProofCodec.Verify(Hex.Parse(step4.Proof));
        var broken = fixture.Rejected.Single(r => r.Name == step4.Document);
        Assert.Equal("rejected", step4.DocumentSet);
        Assert.Equal(keyA, verified.PublicKey);
        Assert.Equal(SHA256.HashData(Hex.Parse(broken.Document)), verified.SubjectDigest.ToArray());
        Assert.Equal(DeploymentName.Parse(step4.Deployment), verified.Deployment);
        Assert.Equal(nameof(ProtocolError.SignatureMismatch), broken.Error);

        // The cross-context vector: its signature is persona A's, valid in the document context over
        // the proof's own fields.
        var cross = Hex.Parse(fixture.RejectedProofs.Single(r => r.Name == "proof-signed-in-the-document-context").Proof);
        var signatureOffset = cross.Length - 64;
        Assert.True(SignatureVerifier.Verify(SigningInput.Create(DocumentType.ProfileSnapshot, keyA, cross.AsSpan(4, signatureOffset - 4)), ProtocolSignature.FromBytes(cross.AsSpan(signatureOffset))));

        // The largest valid proof.
        var longest = fixture.RequestProofs.Single(p => p.Name == "submit-at-the-longest-deployment-name");
        Assert.Equal(ProtocolLimits.MaxRequestProofBytes, Hex.Parse(longest.Proof).Length);
        Assert.Equal(ProtocolLimits.MaxDeploymentNameBytes, longest.Deployment.Length);
        Assert.True(DeploymentName.Parse(longest.Deployment).IsReservedForTesting);
    }

    [Fact]
    public void KeptSignatures_AreOnlyForUnchangedConstructions()
    {
        var fresh = new string('a', 200) + new string('b', 128);
        Assert.Equal(fresh, VectorBuilder.KeepSignature(null, fresh));
        Assert.Equal(new string('a', 200) + new string('c', 128), VectorBuilder.KeepSignature(new string('a', 200) + new string('c', 128), fresh));
        Assert.Equal(fresh, VectorBuilder.KeepSignature("d" + new string('a', 199) + new string('c', 128), fresh));
        Assert.Equal(fresh, VectorBuilder.KeepSignature(new string('a', 201) + new string('c', 128), fresh));
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
        var path = Path.Combine(fixtures, "vectors-v1.json");
        var committed = File.Exists(path) ? JsonSerializer.Deserialize<VectorFixture>(File.ReadAllBytes(path), VectorFixture.Options) : null;
        VectorBuilder.Build(committed).Save(path);

        // What was written must read back as a fixture this build accepts, so a regeneration run is
        // never green on the strength of having written a file.
        var written = JsonSerializer.Deserialize<VectorFixture>(File.ReadAllBytes(path), VectorFixture.Options)!;
        Assert.Equal(ProtocolConstants.ProtocolVersion, written.ProtocolVersion);
        Assert.All(written.Documents.Where(d => d.Document is not null), d => SignedDocumentCodec.Verify(Hex.Parse(d.Document!)));
        Assert.All(written.Rejected, r => ProtocolAssert.Rejects(() => SignedDocumentCodec.Verify(Hex.Parse(r.Document))));
        Assert.All(written.RequestProofs, p => RequestProofCodec.VerifySubmission(Hex.Parse(p.Proof), Hex.Parse(written.Documents.Single(d => d.Name == p.Document).Document!), DeploymentName.Parse(p.Deployment)));
    }
}
