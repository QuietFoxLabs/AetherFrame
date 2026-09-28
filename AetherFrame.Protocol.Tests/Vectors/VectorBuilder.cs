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
    public const string SnapshotUnicodeName = "Caf\u00e9 Cafe\u0301 \U0001F600 \u65e5\u672c \u0627\u0644\u0639\u0631\u0628\u064a\u0629 line\r\nbreak tab\t bom\ufeff zwsp\u200b";

    public sealed record ModelVector(string Name, string Persona, RemoteDocument Model, string? Construction);

    public static IReadOnlyList<ModelVector> Models() =>
    [
        new("profile-snapshot", "A", Samples.Snapshot(), null),
        new("profile-snapshot-unicode", "A", new ProfileSnapshot(Samples.Profile, Samples.Revision, 1_726_000_000, SnapshotUnicodeName, [Samples.Image(Samples.Asset2, 0x7f, ImageFormat.WebP, ProtocolLimits.MaxImageBytes, 8192, 2441)]), null),
        new("profile-snapshot-empty", "A", new ProfileSnapshot(Samples.Profile, Samples.Revision, 0, "", []), null),
        new("profile-snapshot-maximal", "A", PayloadBuilder.MaximalSnapshot(), "name = U+1F600 repeated 32000 times (128000 UTF-8 bytes); createdAt = 253402300799; images = eight references with asset ids ast_ + 31 zeros + 1..8, digest bytes all equal to the index, format png (1), 5242880 bytes each (40 MiB in total), 5000 x 4000 pixels; profile and revision ids as in profile-snapshot. Payload, signing input and document are omitted for size; digest and signature are over exactly that construction."),
        new("profile-retraction", "B", Samples.Retraction(), null),
    ];

    public static VectorFixture Build()
    {
        using var a = TestPersonas.CreateA();
        using var b = TestPersonas.CreateB();
        var signers = new Dictionary<string, EcdsaPersonaSigner> { ["A"] = a, ["B"] = b };

        var fixture = new VectorFixture
        {
            ProtocolVersion = ProtocolConstants.ProtocolVersion,
            SignatureDomainTag = System.Text.Encoding.ASCII.GetString(ProtocolConstants.SignatureDomainTag),
            PersonaIdDomainTag = System.Text.Encoding.ASCII.GetString(ProtocolConstants.PersonaIdDomainTag),
            Notes = "Synthetic test identities only: each private scalar is SHA-256(label) mod n. Signatures are ECDSA P-256 over SHA-256 of the signing input, P1363 r||s with low s; ECDSA is randomized, so a regeneration produces different but equally valid signatures. See docs/networking/ProtocolSpecification-v1.md.",
            Personas =
            [
                Persona("A", TestPersonas.LabelA, TestPersonas.ScalarA, a.PublicKey),
                Persona("B", TestPersonas.LabelB, TestPersonas.ScalarB, b.PublicKey),
            ],
        };

        foreach (var model in Models())
        {
            fixture.Documents.Add(Document(model, signers[model.Persona]));
        }

        var baseDocument = Hex.Parse(fixture.Documents[0].Document!);
        fixture.Rejected = RejectedVectorBuilder.Build(baseDocument, a, b.PublicKey);
        return fixture;
    }

    private static PersonaVector Persona(string name, string label, System.Numerics.BigInteger scalar, PersonaPublicKey key) => new()
    {
        Name = name,
        Label = label,
        PrivateScalar = Hex.Of(ReferenceP256.ToBytes32(scalar)),
        PublicKey = Hex.Of(key.Bytes),
        PersonaId = key.Id.ToString(),
    };

    private static DocumentVector Document(ModelVector model, EcdsaPersonaSigner signer)
    {
        var payload = model.Model.EncodePayload();
        var input = SigningInput.Create(model.Model.DocumentType, signer.PublicKey, payload);
        var signature = signer.Sign(input);
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
