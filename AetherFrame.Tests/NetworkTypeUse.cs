using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace AetherFrame.Tests;

/// <summary>
/// Which types each of an assembly's own types names, read from its compiled metadata (decision
/// R3's "only under Services/Network", checked on the DLL rather than on the sources): its base
/// type, interfaces and generic constraints; its fields', methods', properties' and events'
/// signatures; every custom attribute's constructor; and every token in its methods' IL, with each
/// type specification, member reference, method specification and local signature decoded down
/// to the type references inside. A nested type counts as its outermost declaring type, so a
/// lambda's closure or an iterator's state machine counts as the type that wrote it.
/// </summary>
internal static class NetworkTypeUse
{
    /// <summary>For each outermost type, by its full name, the full names of the types it references.</summary>
    internal static Dictionary<string, HashSet<string>> ReferencesByType(PEReader pe)
    {
        var reader = pe.GetMetadataReader();
        var found = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var handle in reader.TypeDefinitions)
        {
            var names = new TypeNames(reader);
            var type = reader.GetTypeDefinition(handle);
            names.Entity(type.BaseType);
            foreach (var implementation in type.GetInterfaceImplementations())
            {
                names.Entity(reader.GetInterfaceImplementation(implementation).Interface);
            }

            names.Constraints(type.GetGenericParameters());
            names.Attributes(type.GetCustomAttributes());
            foreach (var fieldHandle in type.GetFields())
            {
                var field = reader.GetFieldDefinition(fieldHandle);
                field.DecodeSignature(names, null);
                names.Attributes(field.GetCustomAttributes());
            }

            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                method.DecodeSignature(names, null);
                names.Constraints(method.GetGenericParameters());
                names.Attributes(method.GetCustomAttributes());
                foreach (var parameter in method.GetParameters())
                {
                    names.Attributes(reader.GetParameter(parameter).GetCustomAttributes());
                }

                if (method.RelativeVirtualAddress == 0)
                {
                    continue;
                }

                var body = pe.GetMethodBody(method.RelativeVirtualAddress);
                names.Entity(body.LocalSignature);
                foreach (var (_, token) in IlScan.TokenOperands(body.GetILReader()))
                {
                    names.Token(token);
                }
            }

            foreach (var propertyHandle in type.GetProperties())
            {
                var property = reader.GetPropertyDefinition(propertyHandle);
                property.DecodeSignature(names, null);
                names.Attributes(property.GetCustomAttributes());
            }

            foreach (var eventHandle in type.GetEvents())
            {
                names.Entity(reader.GetEventDefinition(eventHandle).Type);
            }

            var outer = names.NameOf(Outermost(reader, handle));
            if (!found.TryGetValue(outer, out var set))
            {
                found[outer] = set = new HashSet<string>(StringComparer.Ordinal);
            }

            set.UnionWith(names.Referenced);
        }

        return found;
    }

    /// <summary>Every member reference in the assembly: its parent type's full name and its own name.</summary>
    internal static List<(string Parent, string Name)> MemberReferences(MetadataReader reader)
    {
        var names = new TypeNames(reader);
        var list = new List<(string, string)>();
        foreach (var handle in reader.MemberReferences)
        {
            var member = reader.GetMemberReference(handle);
            var parent = member.Parent.Kind switch
            {
                HandleKind.TypeReference => names.NameOf((TypeReferenceHandle)member.Parent),
                HandleKind.TypeSpecification => reader.GetTypeSpecification((TypeSpecificationHandle)member.Parent).DecodeSignature(names, null),
                HandleKind.TypeDefinition => names.NameOf((TypeDefinitionHandle)member.Parent),
                _ => "",
            };
            list.Add((parent, reader.GetString(member.Name)));
        }

        return list;
    }

    private static TypeDefinitionHandle Outermost(MetadataReader reader, TypeDefinitionHandle handle)
    {
        while (true)
        {
            var declaring = reader.GetTypeDefinition(handle).GetDeclaringType();
            if (declaring.IsNil)
            {
                return handle;
            }

            handle = declaring;
        }
    }

    /// <summary>A signature decoder that names each type, keeping every type reference it meets.</summary>
    private sealed class TypeNames(MetadataReader reader) : ISignatureTypeProvider<string, object?>
    {
        internal HashSet<string> Referenced { get; } = new(StringComparer.Ordinal);

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

        public string GetTypeFromDefinition(MetadataReader metadata, TypeDefinitionHandle handle, byte rawTypeKind) => NameOf(handle);

        public string GetTypeFromReference(MetadataReader metadata, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var name = NameOf(handle);
            Referenced.Add(name);
            return name;
        }

        public string GetTypeFromSpecification(MetadataReader metadata, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            metadata.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[*]";

        public string GetByReferenceType(string elementType) => elementType + "&";

        public string GetPointerType(string elementType) => elementType + "*";

        public string GetPinnedType(string elementType) => elementType;

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType + "<" + string.Join(",", typeArguments) + ">";

        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;

        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;

        public string GetFunctionPointerType(MethodSignature<string> signature) => "method " + signature.ReturnType + "(" + string.Join(",", signature.ParameterTypes) + ")";

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

        internal string NameOf(TypeReferenceHandle handle)
        {
            var reference = reader.GetTypeReference(handle);
            var name = reader.GetString(reference.Name);
            if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
            {
                return NameOf((TypeReferenceHandle)reference.ResolutionScope) + "+" + name;
            }

            var ns = reader.GetString(reference.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        internal string NameOf(TypeDefinitionHandle handle)
        {
            var definition = reader.GetTypeDefinition(handle);
            var name = reader.GetString(definition.Name);
            var declaring = definition.GetDeclaringType();
            if (!declaring.IsNil)
            {
                return NameOf(declaring) + "+" + name;
            }

            var ns = reader.GetString(definition.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        internal void Entity(EntityHandle handle)
        {
            if (!handle.IsNil)
            {
                Token(MetadataTokens.GetToken(handle));
            }
        }

        /// <summary>Names the types an IL operand's token refers to; the assembly's own definitions and strings name none.</summary>
        internal void Token(int token)
        {
            var handle = MetadataTokens.Handle(token);
            switch (handle.Kind)
            {
                case HandleKind.TypeReference:
                    GetTypeFromReference(reader, (TypeReferenceHandle)handle, 0);
                    break;
                case HandleKind.TypeSpecification:
                    reader.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(this, null);
                    break;
                case HandleKind.MemberReference:
                    Member((MemberReferenceHandle)handle);
                    break;
                case HandleKind.MethodSpecification:
                    var specification = reader.GetMethodSpecification((MethodSpecificationHandle)handle);
                    Entity(specification.Method);
                    specification.DecodeSignature(this, null);
                    break;
                case HandleKind.StandaloneSignature:
                    var signature = reader.GetStandaloneSignature((StandaloneSignatureHandle)handle);
                    if (signature.GetKind() == StandaloneSignatureKind.Method)
                    {
                        signature.DecodeMethodSignature(this, null);
                    }
                    else
                    {
                        signature.DecodeLocalSignature(this, null);
                    }

                    break;
            }
        }

        internal void Constraints(GenericParameterHandleCollection parameters)
        {
            foreach (var parameter in parameters)
            {
                foreach (var constraint in reader.GetGenericParameter(parameter).GetConstraints())
                {
                    Entity(reader.GetGenericParameterConstraint(constraint).Type);
                }
            }
        }

        internal void Attributes(CustomAttributeHandleCollection attributes)
        {
            foreach (var attribute in attributes)
            {
                Entity(reader.GetCustomAttribute(attribute).Constructor);
            }
        }

        private void Member(MemberReferenceHandle handle)
        {
            var member = reader.GetMemberReference(handle);
            Entity(member.Parent.Kind is HandleKind.TypeReference or HandleKind.TypeSpecification ? (EntityHandle)member.Parent : default);
            if (member.GetKind() == MemberReferenceKind.Method)
            {
                member.DecodeMethodSignature(this, null);
            }
            else
            {
                member.DecodeFieldSignature(this, null);
            }
        }
    }
}
