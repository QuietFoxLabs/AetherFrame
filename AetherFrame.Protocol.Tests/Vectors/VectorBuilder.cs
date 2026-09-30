using System;
using System.Collections.Generic;
using System.Linq;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Remote;
using AetherFrame.Protocol.Signing;

namespace AetherFrame.Protocol.Tests;

/// <summary>Builds the test vectors from the synthetic personas and the sample models.</summary>
internal static class VectorBuilder
{
    /// <summary>A name the name rule (decision D4) accepts, in several scripts and both normalization forms: nothing in it is normalized.</summary>
    public const string SnapshotUnicodeName = "Caf\u00e9 Cafe\u0301 \U0001F600\U0001F3FD \u65e5\u672c \u0627\u0644\u0639\u0631\u0628\u064a\u0629 \u05e9\u05dc\u05d5\u05dd";

    /// <summary>The unicode sample's name before decision D4: it holds a line break, a tab, a byte order mark and a zero width space, all refused now.</summary>
    public const string SnapshotUnicodeNameBeforeD4 = "Caf\u00e9 Cafe\u0301 \U0001F600 \u65e5\u672c \u0627\u0644\u0639\u0631\u0628\u064a\u0629 line\r\nbreak tab\t bom\ufeff zwsp\u200b";

    public sealed record ModelVector(string Name, string Persona, RemoteDocument Model, string? Construction);

    public static IReadOnlyList<ModelVector> Models() =>
    [
        new("profile-snapshot", "A", Samples.Snapshot(), null),
        new("profile-snapshot-unicode", "A", new ProfileSnapshot(Samples.Profile, Samples.RevisionUnicode, 1_726_000_000, SnapshotUnicodeName, [Samples.Image(Samples.Asset2, 0x7f, ImageFormat.WebP, ProtocolLimits.MaxImageBytes, 8192, 2441)]), null),
        new("profile-snapshot-minimal", "A", new ProfileSnapshot(Samples.Profile, Samples.RevisionEmpty, 0, "A", []), null),
        new("profile-snapshot-maximal", "A", PayloadBuilder.MaximalSnapshot(), "name = U+1F600 repeated 64 times (256 UTF-8 bytes, the name's limits); createdAt = 253402300799; images = eight references with asset ids ast_ + 31 zeros + 1..8, digest bytes all equal to the index, format png (1), 5242880 bytes each (40 MiB in total), 5000 x 4000 pixels; profile id as in profile-snapshot and revision id rev_ + b5 repeated 16 times. Payload, signing input and document are omitted for size; digest and signature are over exactly that construction."),
        new("profile-layout-snapshot", "A", LayoutSamples.Rich(), null),
        new("profile-retraction", "B", new ProfileRetraction(Samples.ProfileB, Samples.IssuedAt), null),
    ];

    /// <summary>
    /// Builds every vector. With <paramref name="committed"/>, the fixture already in the source
    /// tree, a vector whose signed bytes have not changed keeps its committed signature: ECDSA is
    /// randomized, so signing unchanged content again would rewrite every vector, and a
    /// regeneration's diff would hide the vectors that did change.
    /// </summary>
    public static VectorFixture Build(VectorFixture? committed = null)
    {
        using var a = TestPersonas.CreateA();
        using var b = TestPersonas.CreateB();
        var signers = new Dictionary<string, EcdsaPersonaSigner> { ["A"] = a, ["B"] = b };

        var fixture = new VectorFixture
        {
            ProtocolVersion = ProtocolConstants.ProtocolVersion,
            SignatureDomainTag = System.Text.Encoding.ASCII.GetString(ProtocolConstants.SignatureDomainTag),
            PersonaIdDomainTag = System.Text.Encoding.ASCII.GetString(ProtocolConstants.PersonaIdDomainTag),
            Notes = "DRAFT protocol (docs/networking/ProtocolSpecification-v1.md is not frozen; these vectors describe the draft as implemented and are regenerated when it changes). Every document carries the draft marker of decision N3: protocolVersion 0x8001 (32769) and the signature tag AetherFrame.Protocol.SignedDocument.v1-draft; a final version 1 document is refused. Synthetic test identities only: each private scalar is SHA-256(label) mod n. Signatures are ECDSA P-256 over SHA-256 of the signing input, P1363 r||s with low s; ECDSA is randomized, so a regeneration produces different but equally valid signatures. A profile is (persona, profileId): the retraction is persona B's, of B's own profile, and the serverObligations documents are valid documents that name another persona's profile id and must be applied to the signing persona's profile only. See docs/networking/ProtocolSpecification-v1.md.",
            Personas =
            [
                Persona("A", TestPersonas.LabelA, TestPersonas.ScalarA, a.PublicKey),
                Persona("B", TestPersonas.LabelB, TestPersonas.ScalarB, b.PublicKey),
            ],
        };

        foreach (var model in Models())
        {
            fixture.Documents.Add(Document(model, signers[model.Persona], committed?.Documents.SingleOrDefault(d => d.Name == model.Name)));
        }

        fixture.Profiles = Samples.ProfileOwners.Select(p => new ProfileOwnerVector { ProfileId = p.Key, Owner = p.Value }).OrderBy(p => p.ProfileId, StringComparer.Ordinal).ToList();

        var baseDocument = Hex.Parse(fixture.Documents[0].Document!);
        var retractionDocument = Hex.Parse(fixture.Documents.Single(d => d.Name == "profile-retraction").Document!);
        fixture.Rejected = RejectedVectorBuilder.Build(baseDocument, retractionDocument, a, b.PublicKey);
        foreach (var vector in fixture.Rejected.Where(r => !r.Deterministic))
        {
            vector.Document = KeepSignature(committed?.Rejected.SingleOrDefault(r => r.Name == vector.Name)?.Document, vector.Document);
        }

        fixture.ServerObligations = ServerObligations(b);
        foreach (var vector in fixture.ServerObligations)
        {
            vector.Document = KeepSignature(committed?.ServerObligations.SingleOrDefault(o => o.Name == vector.Name)?.Document, vector.Document);
        }

        byte[] DocumentNamed(string name) => Hex.Parse(fixture.Documents.Single(d => d.Name == name).Document!);
        fixture.RequestProofDomainTag = System.Text.Encoding.ASCII.GetString(ProtocolConstants.RequestProofDomainTag);
        fixture.RequestProofs = RequestProofVectorBuilder.BuildValid(DocumentNamed, signers);
        foreach (var vector in fixture.RequestProofs)
        {
            if (committed?.RequestProofs.SingleOrDefault(p => p.Name == vector.Name) is { } kept && kept.SigningInput == vector.SigningInput
                && ReferenceP256.Verify(Hex.Parse(fixture.Personas.Single(p => p.Name == vector.Persona).PublicKey), Hex.Parse(vector.Digest), Hex.Parse(kept.Signature)))
            {
                vector.Signature = kept.Signature;
                vector.Proof = kept.Proof;
            }
        }

        byte[] RejectedNamed(string name) => Hex.Parse(fixture.Rejected.Single(r => r.Name == name).Document);
        fixture.RejectedProofs = RequestProofVectorBuilder.BuildRejected(Hex.Parse(fixture.RequestProofs[0].Proof), DocumentNamed, RejectedNamed, a, b);
        foreach (var vector in fixture.RejectedProofs.Where(r => !r.Deterministic))
        {
            vector.Proof = KeepSignature(committed?.RejectedProofs.SingleOrDefault(r => r.Name == vector.Name)?.Proof, vector.Proof);
        }

        fixture.ActionProofs = RequestProofVectorBuilder.BuildActions(signers);
        foreach (var vector in fixture.ActionProofs)
        {
            if (committed?.ActionProofs.SingleOrDefault(p => p.Name == vector.Name) is { } kept && kept.SigningInput == vector.SigningInput
                && ReferenceP256.Verify(Hex.Parse(fixture.Personas.Single(p => p.Name == vector.Persona).PublicKey), Hex.Parse(vector.Digest), Hex.Parse(kept.Signature)))
            {
                vector.Signature = kept.Signature;
                vector.Proof = kept.Proof;
            }
        }

        fixture.RejectedActions = RequestProofVectorBuilder.BuildRejectedActions(fixture.ActionProofs.Single(p => p.Name == "action-lookup"), Hex.Parse(fixture.RequestProofs[0].Proof));
        return fixture;
    }

    /// <summary>
    /// The committed hex when it differs from the fresh one only in its last 64 bytes: the same
    /// construction, signed at another time. Used only for vectors that end in a fresh signature
    /// (the non-deterministic rejected vectors and the server obligations); a deterministic vector
    /// follows from its base exactly and is always rebuilt. The vector tests check every kept vector
    /// as they check a fresh one.
    /// </summary>
    public static string KeepSignature(string? committed, string fresh)
    {
        const int signatureHex = 128;
        if (committed is null || committed.Length != fresh.Length || fresh.Length < signatureHex)
        {
            return fresh;
        }

        return string.CompareOrdinal(committed, 0, fresh, 0, fresh.Length - signatureHex) == 0 ? committed : fresh;
    }

    /// <summary>
    /// Persona B signs documents carrying the profile id persona A's snapshots use. Both verify, as
    /// B, and are about B's profile of that id, which B never published: a server keyed by
    /// (persona, profileId) applies them to nothing of A's.
    /// </summary>
    public static List<ServerObligationVector> ServerObligations(EcdsaPersonaSigner b) =>
    [
        new()
        {
            Name = "retraction-by-another-persona",
            Document = Hex.Of(SignedDocumentCodec.Sign(new ProfileRetraction(Samples.Profile, Samples.IssuedAt), b)),
            Persona = "B",
            ProfileId = Samples.Profile.ToString(),
            Owner = Samples.ProfileOwners[Samples.Profile.ToString()],
            Obligation = "Verifies as persona B and withdraws (B, " + Samples.Profile + "), a profile B never published. Persona A's profile (A, " + Samples.Profile + ") is untouched.",
        },
        new()
        {
            Name = "snapshot-by-another-persona",
            Document = Hex.Of(SignedDocumentCodec.Sign(Samples.Snapshot(), b)),
            Persona = "B",
            ProfileId = Samples.Profile.ToString(),
            Owner = Samples.ProfileOwners[Samples.Profile.ToString()],
            Obligation = "Verifies as persona B and is a revision of (B, " + Samples.Profile + "), unrelated to persona A's profile of the same id; it never becomes a revision of A's profile.",
        },
    ];

    private static PersonaVector Persona(string name, string label, System.Numerics.BigInteger scalar, PersonaPublicKey key) => new()
    {
        Name = name,
        Label = label,
        PrivateScalar = Hex.Of(ReferenceP256.ToBytes32(scalar)),
        PublicKey = Hex.Of(key.Bytes),
        PersonaId = key.Id.ToString(),
    };

    private static DocumentVector Document(ModelVector model, EcdsaPersonaSigner signer, DocumentVector? committed)
    {
        var payload = model.Model.EncodePayload();
        var input = SigningInput.Create(model.Model.DocumentType, signer.PublicKey, payload);
        var signature = committed is not null && committed.Digest == Hex.Of(input.ComputeDigest()) && SignatureVerifier.Verify(input, ProtocolSignature.FromBytes(Hex.Parse(committed.Signature)))
            ? ProtocolSignature.FromBytes(Hex.Parse(committed.Signature))
            : signer.Sign(input);
        var document = SignedDocumentCodec.Assemble(model.Model.DocumentType, signer.PublicKey, payload, signature);
        var full = model.Construction is null;
        return new DocumentVector
        {
            Name = model.Name,
            Persona = model.Persona,
            DocumentType = (byte)model.Model.DocumentType,
            Construction = model.Construction,
            Payload = full ? Hex.Of(payload) : null,
            SigningInput = full ? Hex.Of(input.Bytes) : null,
            Digest = Hex.Of(input.ComputeDigest()),
            Signature = Hex.Of(signature.Bytes),
            Document = full ? Hex.Of(document) : null,
            Snapshot = full && model.Model is ProfileSnapshot snapshot ? Expected(snapshot) : null,
            Retraction = model.Model is ProfileRetraction retraction ? new ExpectedRetraction { ProfileId = retraction.ProfileId.ToString(), IssuedAt = retraction.IssuedAtUnixSeconds } : null,
        };
    }

    public static ExpectedSnapshot Expected(ProfileSnapshot snapshot) => new()
    {
        ProfileId = snapshot.ProfileId.ToString(),
        RevisionId = snapshot.RevisionId.ToString(),
        CreatedAt = snapshot.CreatedAtUnixSeconds,
        Name = snapshot.Name,
        Images = snapshot.Images.Select(i => new ExpectedImage
        {
            AssetId = i.AssetId.ToString(),
            Sha256 = Hex.Of(i.Sha256),
            Format = (byte)i.Format,
            ByteLength = i.ByteLength,
            Width = i.Width,
            Height = i.Height,
        }).ToList(),
    };
}
