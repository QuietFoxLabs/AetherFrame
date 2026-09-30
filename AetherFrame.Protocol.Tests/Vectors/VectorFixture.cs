using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace AetherFrame.Protocol.Tests;

/// <summary>The committed test vectors (Fixtures/vectors-v1.json), documented in docs/networking/ProtocolSpecification-v1.md.</summary>
internal sealed class VectorFixture
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public int ProtocolVersion { get; set; }

    public string SignatureDomainTag { get; set; } = "";

    public string PersonaIdDomainTag { get; set; } = "";

    public string Notes { get; set; } = "";

    public List<PersonaVector> Personas { get; set; } = [];

    public List<DocumentVector> Documents { get; set; } = [];

    public List<RejectedVector> Rejected { get; set; } = [];

    /// <summary>
    /// The owner of each profile id the vectors use, as the record a backend would hold. The protocol
    /// cannot know owners; this table is what the ownership tests check the documents against, so a
    /// document signed by anyone but the recorded owner can only appear under serverObligations.
    /// </summary>
    public List<ProfileOwnerVector> Profiles { get; set; } = [];

    /// <summary>Valid documents that verify but oblige a server to do something in particular (docs/networking/ProtocolSpecification-v1.md, "Server obligations").</summary>
    public List<ServerObligationVector> ServerObligations { get; set; } = [];

    /// <summary>The domain tag of request proofs (docs/networking/ProtocolSpecification-v1.md, section 14).</summary>
    public string RequestProofDomainTag { get; set; } = "";

    /// <summary>Valid request proofs, each for one of <see cref="Documents"/> at one deployment under one challenge.</summary>
    public List<RequestProofVector> RequestProofs { get; set; } = [];

    /// <summary>Request proofs a server refuses when it checks them with the document and deployment named.</summary>
    public List<RejectedProofVector> RejectedProofs { get; set; } = [];

    public static VectorFixture Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "vectors-v1.json");
        return JsonSerializer.Deserialize<VectorFixture>(File.ReadAllBytes(path), Options) ?? throw new InvalidDataException(path);
    }

    public void Save(string path)
    {
        var json = JsonSerializer.Serialize(this, Options) + "\n";
        File.WriteAllBytes(path, new System.Text.UTF8Encoding(false).GetBytes(json));
    }
}

internal sealed class PersonaVector
{
    public string Name { get; set; } = "";

    /// <summary>The private scalar is SHA-256 of this label, reduced modulo n.</summary>
    public string Label { get; set; } = "";

    public string PrivateScalar { get; set; } = "";

    public string PublicKey { get; set; } = "";

    public string PersonaId { get; set; } = "";
}

internal sealed class DocumentVector
{
    public string Name { get; set; } = "";

    public string Persona { get; set; } = "";

    public int DocumentType { get; set; }

    /// <summary>How to rebuild a document too large to store in full.</summary>
    public string? Construction { get; set; }

    public string? Payload { get; set; }

    public string? SigningInput { get; set; }

    public string Digest { get; set; } = "";

    public string Signature { get; set; } = "";

    public string? Document { get; set; }

    public ExpectedSnapshot? Snapshot { get; set; }

    public ExpectedRetraction? Retraction { get; set; }
}

internal sealed class ExpectedImage
{
    public string AssetId { get; set; } = "";

    public string Sha256 { get; set; } = "";

    public int Format { get; set; }

    public long ByteLength { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }
}

internal sealed class ExpectedSnapshot
{
    public string ProfileId { get; set; } = "";

    public string RevisionId { get; set; } = "";

    public long CreatedAt { get; set; }

    public string Name { get; set; } = "";

    public List<ExpectedImage> Images { get; set; } = [];
}

internal sealed class ExpectedRetraction
{
    public string ProfileId { get; set; } = "";

    public long IssuedAt { get; set; }
}

internal sealed class RejectedVector
{
    public string Name { get; set; } = "";

    public string Document { get; set; } = "";

    public string Error { get; set; } = "";

    public string Reason { get; set; } = "";

    /// <summary>False when the vector embeds a fresh signature and so differs on every regeneration.</summary>
    public bool Deterministic { get; set; } = true;
}

internal sealed class ProfileOwnerVector
{
    public string ProfileId { get; set; } = "";

    /// <summary>The persona that published this profile id, by the backend's record.</summary>
    public string Owner { get; set; } = "";
}

internal sealed class ServerObligationVector
{
    public string Name { get; set; } = "";

    public string Document { get; set; } = "";

    /// <summary>The persona the document verifies as: the only persona whose profile it can be about.</summary>
    public string Persona { get; set; } = "";

    /// <summary>The profile id the document carries; together with <see cref="Persona"/>, the profile it is about.</summary>
    public string ProfileId { get; set; } = "";

    /// <summary>The recorded owner of <see cref="ProfileId"/> (see <see cref="VectorFixture.Profiles"/>): never the signer, so the document is valid but is not about that owner's profile.</summary>
    public string Owner { get; set; } = "";

    public string Obligation { get; set; } = "";
}

internal static class VectorPaths
{
    /// <summary>The Fixtures folder in the source tree, for regeneration.</summary>
    public static string SourceFixtures()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AetherFrame.Protocol.Tests.csproj")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "Fixtures");
    }
}

internal sealed class RequestProofVector
{
    public string Name { get; set; } = "";

    public string Persona { get; set; } = "";

    /// <summary>The name of the vector in <see cref="VectorFixture.Documents"/> whose document the proof binds.</summary>
    public string Document { get; set; } = "";

    public string Deployment { get; set; } = "";

    /// <summary>The challenge in its text form, <c>chl_</c> and 64 hex digits.</summary>
    public string Challenge { get; set; } = "";

    /// <summary>SHA-256 of the complete document.</summary>
    public string SubjectDigest { get; set; } = "";

    public string SigningInput { get; set; } = "";

    public string Digest { get; set; } = "";

    public string Signature { get; set; } = "";

    public string Proof { get; set; } = "";
}

internal sealed class RejectedProofVector
{
    public string Name { get; set; } = "";

    public string Proof { get; set; } = "";

    /// <summary>The name of the vector in <see cref="VectorFixture.Documents"/> submitted with the proof.</summary>
    public string Document { get; set; } = "";

    /// <summary>The deployment the server checking the proof is.</summary>
    public string Deployment { get; set; } = "";

    public string Error { get; set; } = "";

    public string Reason { get; set; } = "";

    /// <summary>False when the proof holds a fresh signature, which a regeneration changes.</summary>
    public bool Deterministic { get; set; } = true;
}
