using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace AetherFrame.ReleaseTools;

/// <summary>What a plugin DLL says about itself, read from its PE headers and metadata without loading it.</summary>
public sealed record AssemblyFacts(
    string Name,
    Version Version,
    string? FileVersion,
    string? InformationalVersion,
    Machine Machine,
    PEMagic Magic,
    CorFlags CorFlags,
    IReadOnlyList<AssemblyReferenceFact> References)
{
    public bool IsPe32Plus => Magic == PEMagic.PE32Plus;

    public bool IsIlOnly => (CorFlags & CorFlags.ILOnly) != 0;

    public bool Requires32Bit => (CorFlags & CorFlags.Requires32Bit) != 0;
}

public sealed record AssemblyReferenceFact(string Name, Version Version);

public static class PluginAssemblyInspector
{
    public static AssemblyFacts Inspect(byte[] bytes, string what)
    {
        try
        {
            using var pe = new PEReader(ImmutableArray.Create(bytes));
            if (!pe.HasMetadata)
            {
                throw new ReleaseCheckException($"{what} is not a .NET assembly (no metadata).");
            }

            var headers = pe.PEHeaders;
            var corHeader = headers.CorHeader ?? throw new ReleaseCheckException($"{what} has no CLI header.");
            var metadata = pe.GetMetadataReader();
            var assembly = metadata.GetAssemblyDefinition();

            string? fileVersion = null;
            string? informationalVersion = null;
            foreach (var handle in assembly.GetCustomAttributes())
            {
                var attribute = metadata.GetCustomAttribute(handle);
                switch (AttributeTypeName(metadata, attribute.Constructor))
                {
                    case "System.Reflection.AssemblyFileVersionAttribute":
                        fileVersion = SingleStringArgument(metadata, attribute);
                        break;
                    case "System.Reflection.AssemblyInformationalVersionAttribute":
                        informationalVersion = SingleStringArgument(metadata, attribute);
                        break;
                }
            }

            var references = new List<AssemblyReferenceFact>();
            foreach (var handle in metadata.AssemblyReferences)
            {
                var reference = metadata.GetAssemblyReference(handle);
                references.Add(new AssemblyReferenceFact(metadata.GetString(reference.Name), reference.Version));
            }

            return new AssemblyFacts(
                metadata.GetString(assembly.Name),
                assembly.Version,
                fileVersion,
                informationalVersion,
                headers.CoffHeader.Machine,
                headers.PEHeader?.Magic ?? default,
                corHeader.Flags,
                references);
        }
        catch (BadImageFormatException e)
        {
            throw new ReleaseCheckException($"{what} is not a valid PE file: {e.Message}");
        }
        catch (InvalidOperationException e)
        {
            throw new ReleaseCheckException($"{what} could not be read as a PE file: {e.Message}");
        }
    }

    private static string? AttributeTypeName(MetadataReader metadata, EntityHandle constructor)
    {
        switch (constructor.Kind)
        {
            case HandleKind.MemberReference:
                var member = metadata.GetMemberReference((MemberReferenceHandle)constructor);
                if (member.Parent.Kind == HandleKind.TypeReference)
                {
                    var type = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
                    return metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name);
                }

                return null;
            case HandleKind.MethodDefinition:
                var method = metadata.GetMethodDefinition((MethodDefinitionHandle)constructor);
                var declaring = metadata.GetTypeDefinition(method.GetDeclaringType());
                return metadata.GetString(declaring.Namespace) + "." + metadata.GetString(declaring.Name);
            default:
                return null;
        }
    }

    private static string? SingleStringArgument(MetadataReader metadata, CustomAttribute attribute)
    {
        var reader = metadata.GetBlobReader(attribute.Value);
        if (reader.Length < 2 || reader.ReadUInt16() != 1)
        {
            return null;
        }

        return reader.ReadSerializedString();
    }
}
