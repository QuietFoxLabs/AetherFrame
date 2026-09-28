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
    private static readonly JsonSerializerOptions Options = new()
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
