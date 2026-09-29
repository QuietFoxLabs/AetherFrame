using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using Xunit;

namespace AetherFrame.Personas.Tests;

/// <summary>
/// The persona assembly stays where this increment put it: no Dalamud, no game, no network, no
/// files, no Plate or character concept anywhere in it, no private key material on its public
/// surface, and a public surface that changes only on purpose.
/// </summary>
public class AssemblyBoundaryTests
{
    private static readonly Assembly Personas = typeof(PersonaManager).Assembly;

    [Fact]
    public void Personas_ReferencesOnlyTheRuntimeAndTheProtocol()
    {
        var references = Personas.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
        Assert.NotEmpty(references);
        Assert.Contains("AetherFrame.Protocol", references);
        foreach (var name in references)
        {
            Assert.True(name == "AetherFrame.Protocol" || name.StartsWith("System.", StringComparison.Ordinal), name);
            Assert.DoesNotContain("Dalamud", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ImGui", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Lumina", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("FFXIV", name, StringComparison.OrdinalIgnoreCase);
            Assert.False(name.StartsWith("System.Net", StringComparison.Ordinal), name);
            Assert.NotEqual("System.IO.FileSystem", name);
            Assert.NotEqual("System.Diagnostics.Process", name);
            Assert.NotEqual("System.Text.Json", name);
        }
    }

    [Fact]
    public void Personas_UsesNoFileNetworkProcessOrEnvironmentType()
    {
        // The assembly-name check above cannot see file access: on .NET 10, File, FileStream and
        // Directory live in System.Runtime, which every assembly references. So this reads every type
        // the compiled assembly refers to (its TypeRef table: a call to File.WriteAllText needs a
        // reference to System.IO.File) and allows only the namespaces the model needs, and within
        // System none of the types that reach the file system, the environment or the console.
        var allowed = new[]
        {
            "AetherFrame.Protocol", "AetherFrame.Protocol.Identity", "AetherFrame.Protocol.Signing",
            "System", "System.Buffers.Binary", "System.Collections.Generic", "System.Security.Cryptography",
            "System.Threading", "System.Runtime.CompilerServices", "System.Runtime.InteropServices", "System.Runtime.Versioning",
        };
        var allowedTypes = new[] { "System.Diagnostics.DebuggableAttribute", "System.Diagnostics.DebuggableAttribute.DebuggingModes" };
        var refusedInSystem = new[] { "Console", "Environment", "AppContext", "AppDomain", "Activator", "GC", "Uri", "UriBuilder" };

        var referenced = ReferencedTypes(Personas.Location);
        Assert.Contains(("System.Security.Cryptography", "ECDsa"), referenced);
        foreach (var (ns, name) in referenced)
        {
            var full = ns + "." + name;
            var isAssemblyAttribute = ns == "System.Reflection" && name.StartsWith("Assembly", StringComparison.Ordinal) && name.EndsWith("Attribute", StringComparison.Ordinal);
            Assert.True(allowed.Contains(ns) || allowedTypes.Contains(full) || isAssemblyAttribute, $"The persona assembly refers to {full}.");
            Assert.False(ns == "System" && refusedInSystem.Contains(name), $"The persona assembly refers to {full}.");
        }
    }

    [Fact]
    public void Personas_NamesNoCharacterPlateOrNetworkConceptAnywhere()
    {
        // Not even privately: the model has no field, parameter or member through which a character
        // binding, a Content ID, a Plate, a network address or a file path could enter or leave.
        var forbidden = new[] { "Character", "ContentId", "Plate", "Profile", "World", "Account", "Binding", "Http", "Socket", "Uri", "Path", "File", "Guid" };
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var type in Personas.GetTypes().Where(t => !t.Name.StartsWith('<')))
        {
            foreach (var word in forbidden)
            {
                Assert.DoesNotContain(word, type.Name, StringComparison.Ordinal);
            }

            foreach (var member in type.GetMembers(all))
            {
                var name = member.Name;
                var related = member switch
                {
                    MethodInfo m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType),
                    ConstructorInfo c => c.GetParameters().Select(p => p.ParameterType),
                    PropertyInfo p => [p.PropertyType],
                    FieldInfo f => [f.FieldType],
                    _ => [],
                };
                foreach (var word in forbidden)
                {
                    Assert.DoesNotContain(word, name, StringComparison.Ordinal);
                    foreach (var relatedType in related)
                    {
                        Assert.DoesNotContain(word, Describe(relatedType), StringComparison.Ordinal);
                    }
                }
            }
        }
    }

    [Fact]
    public void PublicSurface_ExposesNoPrivateKeyMaterial()
    {
        // The only public members that produce bytes are the backup ones, which produce a codec's
        // protected container by contract, and the storage seams: a protector's blob (Protect), the
        // envelope a storage holds (Read), and the one seam through which a scalar returns to the
        // store from the platform's protection (Unprotect), which only custody code calls. Nothing
        // public returns a platform key, its parameters, or anything named for a private value.
        var byteProducers = new[] { "ExportBackup", "Write", "Protect", "Unprotect", "Read" };
        foreach (var type in Personas.GetExportedTypes())
        {
            Assert.True(type.IsEnum || type.IsInterface || type.IsSealed || type.IsAbstract, $"{type.Name} is neither sealed, static, abstract, an enum nor an interface");
            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                Assert.DoesNotContain("Private", member.Name, StringComparison.Ordinal);
                Assert.DoesNotContain("Scalar", member.Name, StringComparison.Ordinal);
                var returned = member switch
                {
                    MethodInfo method => method.ReturnType,
                    PropertyInfo property => property.PropertyType,
                    FieldInfo field => field.FieldType,
                    _ => typeof(void),
                };
                Assert.False(typeof(ECDsa).IsAssignableFrom(returned), $"{type.Name}.{member.Name} returns a platform key");
                Assert.NotEqual(typeof(ECParameters), returned);
                Assert.NotEqual(typeof(ECParameters), Nullable.GetUnderlyingType(returned));
                if (returned == typeof(byte[]) || returned == typeof(ReadOnlySpan<byte>) || returned == typeof(Span<byte>))
                {
                    Assert.Contains(member.Name, byteProducers);
                }

                Assert.False(!type.IsEnum && member is FieldInfo { IsLiteral: false, IsInitOnly: false }, $"{type.Name}.{member.Name} is a mutable public field");
            }
        }

        // The seam through which custody code reads the scalar is internal, so no code outside this
        // assembly (and its tests) can reach it.
        var export = typeof(PersonaKeyMaterial).GetMethod("ExportPrivateParameters", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(export);
        Assert.True(export!.IsAssembly);
        Assert.Null(typeof(PersonaKeyMaterial).GetMethod("ExportPrivateParameters", BindingFlags.Public | BindingFlags.Instance));
        var secretText = typeof(PersonaBackupSecret).GetProperty("Text", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(secretText);
        Assert.Null(typeof(PersonaBackupSecret).GetProperty("Text", BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public void Records_CannotBeMadeOutsideTheManager()
    {
        // A record exists only for a key the store produced or adopted; nothing else can mint one,
        // so a listing never shows an identity the installation does not hold.
        Assert.Empty(typeof(PersonaRecord).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Empty(typeof(PersonaSignerLease).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Empty(typeof(PersonaRestoreResult).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Null(typeof(PersonaRecord).GetProperty("Label")!.GetSetMethod());
    }

    [Fact]
    public void ToString_NeverRevealsAnIdentityALabelOrASecret()
    {
        var store = new InMemoryPersonaKeyStore();
        var manager = new PersonaManager(store, new HandleBackupCodec());
        var record = manager.Create("Alt for a specific someone");
        manager.Select(record.Slot);
        manager.TryOpenActiveSigner(out var opened);
        using var lease = opened!;
        using var secret = PersonaBackupSecret.FromText("hunter2 hunter2 hunter2");

        foreach (var text in new[] { record.ToString(), lease.ToString(), store.Held(record.Slot).ToString(), secret.ToString() })
        {
            Assert.DoesNotContain(record.Id.ToString(), text, StringComparison.Ordinal);
            Assert.DoesNotContain("psn_", text, StringComparison.Ordinal);
            Assert.DoesNotContain("someone", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
            Assert.DoesNotContain(SyntheticKeys.PublicHex(record.PublicKey).Substring(2, 16), text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(record.Slot.ToString(), record.ToString());
        Assert.Equal(record.Slot.ToString(), lease.ToString());
    }

    [Fact]
    public void ExceptionMessages_NameRulesNotValues()
    {
        var store = new InMemoryPersonaKeyStore();
        var manager = new PersonaManager(store, new HandleBackupCodec());
        var record = manager.Create("Main");
        var messages = new List<string>
        {
            Assert.Throws<PersonaException>(() => manager.Create("bad\u0000label")).Message,
            Assert.Throws<PersonaException>(() => manager.Select(PersonaSlotId.NewId())).Message,
            Assert.Throws<PersonaException>(() => manager.Rename(record.Slot, "")).Message,
            Assert.Throws<PersonaException>(() => PersonaSlotId.Parse("slot_nope")).Message,
        };
        store.Lock(record.Slot);
        using var secret = PersonaBackupSecret.FromText("a secret phrase");
        messages.Add(Assert.Throws<PersonaException>(() => manager.ExportBackup(record.Slot, secret)).Message);

        foreach (var message in messages)
        {
            Assert.DoesNotContain("psn_", message, StringComparison.Ordinal);
            Assert.DoesNotContain("slot_", message, StringComparison.Ordinal);
            Assert.DoesNotContain("bad", message, StringComparison.Ordinal);
            Assert.DoesNotContain("Main", message, StringComparison.Ordinal);
            Assert.DoesNotContain("secret phrase", message, StringComparison.Ordinal);
            var run = 0;
            foreach (var c in message)
            {
                run = Uri.IsHexDigit(c) ? run + 1 : 0;
                Assert.True(run < 24, "The message looks like it contains raw bytes: " + message);
            }
        }
    }

    [Fact]
    public void PublicSurface_MatchesTheApprovedList()
    {
        // A change to the public surface is approved by regenerating the list on purpose, with a
        // switch of its own: AETHERFRAME_PERSONAS_REGENERATE_PUBLIC_API=1.
        var actual = DescribePublicSurface();
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "public-api.txt");
        if (Environment.GetEnvironmentVariable("AETHERFRAME_PERSONAS_REGENERATE_PUBLIC_API") is { Length: > 0 })
        {
            var source = Path.Combine(SourceFixtures(), "public-api.txt");
            File.WriteAllText(source, actual);
            Assert.Equal(actual, File.ReadAllText(source));
            return;
        }

        Assert.Equal(File.ReadAllText(path), actual);
    }

    /// <summary>Every type the assembly's metadata refers to: its namespace (a nested type's is its outermost type's) and its name as Outer.Inner.</summary>
    private static List<(string Namespace, string Name)> ReferencedTypes(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var types = new List<(string, string)>();
        foreach (var handle in metadata.TypeReferences)
        {
            types.Add(Describe(metadata, handle));
        }

        return types;
    }

    private static (string Namespace, string Name) Describe(MetadataReader metadata, TypeReferenceHandle handle)
    {
        var type = metadata.GetTypeReference(handle);
        var name = metadata.GetString(type.Name);
        if (type.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            var outer = Describe(metadata, (TypeReferenceHandle)type.ResolutionScope);
            return (outer.Namespace, outer.Name + "." + name);
        }

        return (metadata.GetString(type.Namespace), name);
    }

    private static string SourceFixtures()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AetherFrame.Personas.Tests.csproj")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "Fixtures");
    }

    private static string DescribePublicSurface()
    {
        var lines = new List<string>();
        foreach (var type in Personas.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            var kind = type.IsEnum ? "enum" : type.IsInterface ? "interface" : type.IsValueType ? "struct" : type.IsAbstract && type.IsSealed ? "static class" : type.IsAbstract ? "abstract class" : "sealed class";
            lines.Add($"{kind} {type.FullName}");
            var members = type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => m is not ConstructorInfo || !type.IsEnum)
                .Where(m => m is not MethodInfo { IsSpecialName: true })
                .Select(Describe)
                .OrderBy(s => s, StringComparer.Ordinal);
            lines.AddRange(members.Select(m => "    " + m));
        }

        return string.Join("\n", lines) + "\n";
    }

    private static string Describe(MemberInfo member) => member switch
    {
        MethodInfo m => $"{Describe(m.ReturnType)} {m.Name}({string.Join(", ", m.GetParameters().Select(p => Describe(p.ParameterType) + " " + p.Name))})",
        ConstructorInfo c => $"new({string.Join(", ", c.GetParameters().Select(p => Describe(p.ParameterType) + " " + p.Name))})",
        PropertyInfo p => $"{Describe(p.PropertyType)} {p.Name} {{ {(p.CanRead ? "get; " : "")}{(p.CanWrite ? "set; " : "")}}}",
        FieldInfo f => $"{(f.IsLiteral ? "const " : "")}{Describe(f.FieldType)} {f.Name}",
        _ => member.Name,
    };

    private static string Describe(Type type) => type.IsGenericType
        ? type.Name.Split('`')[0] + "<" + string.Join(", ", type.GetGenericArguments().Select(Describe)) + ">"
        : type.IsByRef ? "ref " + Describe(type.GetElementType()!) : type.Name;
}
