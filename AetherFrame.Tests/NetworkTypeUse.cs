using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace AetherFrame.Tests;

/// <summary>One use of a member reference in IL: see <see cref="NetworkTypeUse.MemberUses"/>.</summary>
internal sealed record IlMemberUse(string Type, ILOpCode OpCode, string Parent, string Name, int Parameters, ILOpCode Previous);

/// <summary>
/// One instruction of a method body, as <see cref="NetworkTypeUse.MethodBodies"/> reads it: the
/// member its token names (parent type, name, parameter types; a field's one type), the string an
/// <c>ldstr</c> loads, or the integer an <c>ldc.i4</c> form loads.
/// </summary>
internal sealed record IlStep(ILOpCode OpCode, string? Parent, string? Name, string[] Parameters, string? Text, int? Constant)
{
    /// <summary>Whether this is a call or an object's construction: anything that runs code the scan doesn't follow.</summary>
    internal bool Calls => OpCode is ILOpCode.Call or ILOpCode.Callvirt or ILOpCode.Newobj or ILOpCode.Calli;

    public override string ToString() => OpCode + " " + (Text is { } text ? "\"" + text + "\"" : Constant?.ToString() ?? (Parent + "::" + Name + "(" + string.Join(",", Parameters) + ")"));
}

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
    /// <summary>How a type used as a generic type argument (a type's or a method's) is listed among the names a type references.</summary>
    internal const string TypeArgumentPrefix = "type argument ";

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
                foreach (var region in body.ExceptionRegions)
                {
                    names.Entity(region.CatchType);
                }

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

    /// <summary>
    /// Every instruction in the assembly's method bodies whose operand is a member reference: the
    /// outermost type whose method holds it, the opcode, the member's parent type and name, its
    /// parameter count (a field's is -1), and the opcode just before it.
    /// </summary>
    internal static List<IlMemberUse> MemberUses(PEReader pe)
    {
        var reader = pe.GetMetadataReader();
        var names = new TypeNames(reader);
        var uses = new List<IlMemberUse>();
        foreach (var handle in reader.TypeDefinitions)
        {
            var outer = names.NameOf(Outermost(reader, handle));
            foreach (var methodHandle in reader.GetTypeDefinition(handle).GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0)
                {
                    continue;
                }

                var instructions = IlScan.Instructions(pe.GetMethodBody(method.RelativeVirtualAddress).GetILReader());
                for (var index = 0; index < instructions.Count; index++)
                {
                    var (opCode, token) = instructions[index];
                    if (token == 0 || MetadataTokens.Handle(token).Kind != HandleKind.MemberReference)
                    {
                        continue;
                    }

                    var member = reader.GetMemberReference((MemberReferenceHandle)MetadataTokens.Handle(token));
                    var parent = member.Parent.Kind switch
                    {
                        HandleKind.TypeReference => names.NameOf((TypeReferenceHandle)member.Parent),
                        HandleKind.TypeSpecification => reader.GetTypeSpecification((TypeSpecificationHandle)member.Parent).DecodeSignature(names, null),
                        HandleKind.TypeDefinition => names.NameOf((TypeDefinitionHandle)member.Parent),
                        _ => "",
                    };
                    var parameters = member.GetKind() == MemberReferenceKind.Method ? member.DecodeMethodSignature(names, null).ParameterTypes.Length : -1;
                    uses.Add(new IlMemberUse(outer, opCode, parent, reader.GetString(member.Name), parameters, index > 0 ? instructions[index - 1].OpCode : ILOpCode.Nop));
                }
            }
        }

        return uses;
    }

    /// <summary>
    /// Every method body of the types whose outermost type is <paramref name="outermost"/> (its
    /// nested types, closures and state machines included), each as its instructions in order:
    /// the member a token names (a member reference, a method specification's method, or one of
    /// the assembly's own methods and fields) with its parameter types (a field's one type), the
    /// string an <c>ldstr</c> loads, and the integer an <c>ldc.i4</c> form loads.
    /// </summary>
    internal static List<(string Method, List<IlStep> Steps)> MethodBodies(PEReader pe, string outermost)
    {
        var reader = pe.GetMetadataReader();
        var names = new TypeNames(reader);
        var bodies = new List<(string, List<IlStep>)>();
        foreach (var handle in reader.TypeDefinitions)
        {
            if (names.NameOf(Outermost(reader, handle)) != outermost)
            {
                continue;
            }

            foreach (var methodHandle in reader.GetTypeDefinition(handle).GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0)
                {
                    continue;
                }

                var steps = new List<IlStep>();
                foreach (var (opCode, token, constant) in IlScan.InstructionsWithConstants(pe.GetMethodBody(method.RelativeVirtualAddress).GetILReader()))
                {
                    if (opCode == ILOpCode.Ldstr)
                    {
                        steps.Add(new IlStep(opCode, null, null, [], reader.GetUserString(MetadataTokens.UserStringHandle(token & 0xFFFFFF)), null));
                        continue;
                    }

                    var (parent, name, parameters) = token == 0 ? (null, null, []) : Named(reader, names, MetadataTokens.EntityHandle(token));
                    steps.Add(new IlStep(opCode, parent, name, parameters, null, constant));
                }

                bodies.Add((names.NameOf(handle) + "." + reader.GetString(method.Name), steps));
            }
        }

        return bodies;
    }

    /// <summary>
    /// The surface that code outside <paramref name="outermost"/> can reach: each of its types not
    /// hidden by a private nesting (itself, or a type it is nested in), with every member that isn't
    /// private, and the types each one names. A type's own entry (its name alone) holds its base
    /// type and interfaces; a method's, its return and parameter types; a field's, its type. A
    /// property's or event's accessors are methods, so their types are listed through them.
    /// </summary>
    internal static List<(string Member, HashSet<string> Names)> NonPrivateSurface(PEReader pe, string outermost)
    {
        var reader = pe.GetMetadataReader();
        var typeNames = new TypeNames(reader);
        var surface = new List<(string, HashSet<string>)>();
        foreach (var handle in reader.TypeDefinitions)
        {
            if (typeNames.NameOf(Outermost(reader, handle)) != outermost || HiddenByNesting(reader, handle))
            {
                continue;
            }

            var type = reader.GetTypeDefinition(handle);
            var name = typeNames.NameOf(handle);
            var names = new TypeNames(reader);
            names.Entity(type.BaseType);
            foreach (var implementation in type.GetInterfaceImplementations())
            {
                names.Entity(reader.GetInterfaceImplementation(implementation).Interface);
            }

            surface.Add((name, names.Referenced));
            foreach (var fieldHandle in type.GetFields())
            {
                var field = reader.GetFieldDefinition(fieldHandle);
                if ((field.Attributes & System.Reflection.FieldAttributes.FieldAccessMask) is System.Reflection.FieldAttributes.Private or System.Reflection.FieldAttributes.PrivateScope)
                {
                    continue;
                }

                names = new TypeNames(reader);
                field.DecodeSignature(names, null);
                surface.Add((name + "." + reader.GetString(field.Name), names.Referenced));
            }

            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if ((method.Attributes & System.Reflection.MethodAttributes.MemberAccessMask) is System.Reflection.MethodAttributes.Private or System.Reflection.MethodAttributes.PrivateScope)
                {
                    continue;
                }

                names = new TypeNames(reader);
                var signature = method.DecodeSignature(names, null);
                surface.Add((name + "." + reader.GetString(method.Name) + "(" + string.Join(",", signature.ParameterTypes) + ")", names.Referenced));
            }
        }

        return surface;
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

    /// <summary>The parent type, name and parameter types (a field's one type) of the member a token names; nulls for any other token.</summary>
    private static (string? Parent, string? Name, string[] Parameters) Named(MetadataReader reader, TypeNames names, EntityHandle handle)
    {
        switch (handle.Kind)
        {
            case HandleKind.MemberReference:
                var member = reader.GetMemberReference((MemberReferenceHandle)handle);
                var parent = member.Parent.Kind switch
                {
                    HandleKind.TypeReference => names.NameOf((TypeReferenceHandle)member.Parent),
                    HandleKind.TypeSpecification => reader.GetTypeSpecification((TypeSpecificationHandle)member.Parent).DecodeSignature(names, null),
                    HandleKind.TypeDefinition => names.NameOf((TypeDefinitionHandle)member.Parent),
                    _ => "",
                };
                string[] parameters = member.GetKind() == MemberReferenceKind.Method
                    ? [.. member.DecodeMethodSignature(names, null).ParameterTypes]
                    : [member.DecodeFieldSignature(names, null)];
                return (parent, reader.GetString(member.Name), parameters);
            case HandleKind.MethodSpecification:
                return Named(reader, names, reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method);
            case HandleKind.MethodDefinition:
                var method = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
                return (names.NameOf(method.GetDeclaringType()), reader.GetString(method.Name), [.. method.DecodeSignature(names, null).ParameterTypes]);
            case HandleKind.FieldDefinition:
                var field = reader.GetFieldDefinition((FieldDefinitionHandle)handle);
                return (names.NameOf(field.GetDeclaringType()), reader.GetString(field.Name), [field.DecodeSignature(names, null)]);
            default:
                return (null, null, []);
        }
    }

    private static bool HiddenByNesting(MetadataReader reader, TypeDefinitionHandle handle)
    {
        for (var type = reader.GetTypeDefinition(handle); !type.GetDeclaringType().IsNil; type = reader.GetTypeDefinition(type.GetDeclaringType()))
        {
            if ((type.Attributes & System.Reflection.TypeAttributes.VisibilityMask) == System.Reflection.TypeAttributes.NestedPrivate)
            {
                return true;
            }
        }

        return false;
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

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
        {
            foreach (var argument in typeArguments)
            {
                Referenced.Add(TypeArgumentPrefix + argument);
            }

            return genericType + "<" + string.Join(",", typeArguments) + ">";
        }

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
                    foreach (var argument in specification.DecodeSignature(this, null))
                    {
                        Referenced.Add(TypeArgumentPrefix + argument);
                    }

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
