using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Signing;
using Xunit;

namespace AetherFrame.Protocol.Tests;

/// <summary>
/// The protocol assembly stays where NETWORK0 put it: no Dalamud, no game, no network, no files,
/// no private key material on its public surface, and a public surface that changes only on purpose.
/// </summary>
public class AssemblyBoundaryTests
{
    private static readonly Assembly Protocol = typeof(SignedDocumentCodec).Assembly;

    [Fact]
    public void Protocol_ReferencesNoDalamudGameNetworkOrFileSystemAssemblies()
    {
        var references = Protocol.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
        Assert.NotEmpty(references);
        foreach (var name in references)
        {
            Assert.True(name == "System.Runtime" || name.StartsWith("System.", StringComparison.Ordinal), name);
            Assert.DoesNotContain("Dalamud", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ImGui", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Lumina", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("FFXIV", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("AetherFrame", name, StringComparison.OrdinalIgnoreCase);
            Assert.False(name.StartsWith("System.Net", StringComparison.Ordinal), name);
            Assert.NotEqual("System.IO.FileSystem", name);
            Assert.NotEqual("System.Diagnostics.Process", name);
            Assert.NotEqual("System.Text.Json", name);
        }
    }

    [Fact]
    public void PublicSurface_ExposesNoPrivateKeyMaterialAndNoPlatformKeys()
    {
        foreach (var type in Protocol.GetExportedTypes())
        {
            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                Assert.DoesNotContain("Private", member.Name, StringComparison.Ordinal);
                Assert.DoesNotContain("Export", member.Name, StringComparison.Ordinal);
                Assert.DoesNotContain("Secret", member.Name, StringComparison.Ordinal);
                var returned = member switch
                {
                    MethodInfo method => method.ReturnType,
                    PropertyInfo property => property.PropertyType,
                    FieldInfo field => field.FieldType,
                    _ => typeof(void),
                };
                Assert.False(typeof(ECDsa).IsAssignableFrom(returned), $"{type.Name}.{member.Name} returns a platform key");
                Assert.NotEqual(typeof(ECParameters), returned);
                Assert.False(!type.IsEnum && member is FieldInfo { IsLiteral: false, IsInitOnly: false }, $"{type.Name}.{member.Name} is a mutable public field");
            }
        }
    }

    [Fact]
    public void RemoteDocumentHierarchy_IsClosed()
    {
        var subtypes = Protocol.GetExportedTypes().Where(t => t.IsSubclassOf(typeof(RemoteDocument))).OrderBy(t => t.Name).ToArray();
        Assert.Equal(["ProfileRetraction", "ProfileSnapshot"], subtypes.Select(t => t.Name));
        Assert.All(subtypes, t => Assert.True(t.IsSealed));
        Assert.Empty(typeof(RemoteDocument).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        foreach (var type in Protocol.GetExportedTypes().Where(t => t.IsClass && t != typeof(RemoteDocument)))
        {
            Assert.True(type.IsSealed || type.IsAbstract, $"{type.Name} is neither sealed nor static");
        }
    }

    [Fact]
    public void ToString_NeverRevealsKeyOrSignatureBytes()
    {
        using var signer = TestPersonas.CreateA();
        var keyHex = Hex.Of(signer.PublicKey.Bytes);
        Assert.Equal(signer.PublicKey.Id.ToString(), signer.PublicKey.ToString());
        Assert.DoesNotContain(keyHex.Substring(2, 16), signer.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
        var signature = signer.Sign(SigningInput.Create(DocumentType.ProfileRetraction, signer.PublicKey, Samples.Retraction().EncodePayload()));
        Assert.DoesNotContain(Hex.Of(signature.Bytes).Substring(0, 16), signature.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PublicSurface_MatchesTheApprovedList()
    {
        // A change to the public surface is approved by regenerating the list on purpose, with a
        // switch of its own: AETHERFRAME_PROTOCOL_REGENERATE_PUBLIC_API=1 (the vectors have theirs).
        var actual = DescribePublicSurface();
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "public-api.txt");
        if (Environment.GetEnvironmentVariable("AETHERFRAME_PROTOCOL_REGENERATE_PUBLIC_API") is { Length: > 0 })
        {
            var source = Path.Combine(VectorPaths.SourceFixtures(), "public-api.txt");
            File.WriteAllText(source, actual);
            Assert.Equal(actual, File.ReadAllText(source));
            return;
        }

        Assert.Equal(File.ReadAllText(path), actual);
    }

    private static string DescribePublicSurface()
    {
        var lines = new List<string>();
        foreach (var type in Protocol.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
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
        MethodInfo m => $"{Name(m.ReturnType)} {m.Name}({string.Join(", ", m.GetParameters().Select(p => Name(p.ParameterType) + " " + p.Name))})",
        ConstructorInfo c => $"new({string.Join(", ", c.GetParameters().Select(p => Name(p.ParameterType) + " " + p.Name))})",
        PropertyInfo p => $"{Name(p.PropertyType)} {p.Name} {{ {(p.CanRead ? "get; " : "")}{(p.CanWrite ? "set; " : "")}}}",
        FieldInfo f => $"{(f.IsLiteral ? "const " : "")}{Name(f.FieldType)} {f.Name}",
        _ => member.Name,
    };

    private static string Name(Type type) => type.IsGenericType
        ? type.Name.Split('`')[0] + "<" + string.Join(", ", type.GetGenericArguments().Select(Name)) + ">"
        : type.IsByRef ? "ref " + Name(type.GetElementType()!) : type.Name;
}
