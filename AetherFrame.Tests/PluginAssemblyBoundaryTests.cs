using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The boundaries of the built plugin (docs/networking/NETWORK1.md, section 3). The player build
/// reaches nothing online and holds nothing experimental: no networking assembly, no networking
/// API, and no protocol or persona code. The networking preview flavour (built with
/// <c>AetherFrameNetworkPreview=true</c>) holds the protocol and persona code compiled in, and
/// keeps every other boundary; CI tells these tests which flavour they are looking at through
/// <c>AETHERFRAME_PLUGIN_FLAVOUR</c>. Checked against the DLL that ships (CI names it in
/// <c>AETHERFRAME_PLUGIN_ASSEMBLY</c>), or the local build output when there is one. The plugin's
/// own sources are held to the same lines: local folders never name the networking code, nothing
/// uses a networking API, and the configuration has no persona members.
/// </summary>
public class PluginAssemblyBoundaryTests
{
    private static readonly string[] ForbiddenAssemblyPrefixes =
    [
        "System.Net.Http",
        "System.Net.Sockets",
        "System.Net.WebSockets",
        "System.Net.Requests",
        "System.Net.Mail",
        "System.Net.Security",
        "System.Net.Quic",
        "AetherFrame.Protocol",
        "AetherFrame.Personas",
    ];

    /// <summary>The namespaces the networking code lives in, whichever assembly compiles it.</summary>
    private static readonly string[] NetworkingNamespaces = ["AetherFrame.Protocol", "AetherFrame.Personas"];

    /// <summary>The namespaces of the plugin's own networking folders, which a player build does not compile.</summary>
    private static readonly string[] PluginNetworkingNamespaces = ["AetherFrame.Services.Network", "AetherFrame.Hosting.Network", "AetherFrame.Windows.Network"];

    /// <summary>What a plugin source outside the networking folders may never name.</summary>
    private static readonly string[] NetworkingNames = ["AetherFrame.Protocol", "AetherFrame.Personas", "Services.Network", "Hosting.Network", "Windows.Network"];

    /// <summary>Networking APIs no plugin source may use, in any flavour.</summary>
    private static readonly string[] NetworkingApis = ["HttpClient", "WebRequest", "WebClient", "System.Net.", "Sockets", "OpenLink", "Dns."];

    /// <summary>The folders that will hold the networking code inside the plugin; nothing else may name it.</summary>
    private static readonly string[] NetworkingFolders =
    [
        Path.Combine("Services", "Network"),
        Path.Combine("Hosting", "Network"),
        Path.Combine("Windows", "Network"),
    ];

    private static bool PreviewFlavour =>
        string.Equals(Environment.GetEnvironmentVariable("AETHERFRAME_PLUGIN_FLAVOUR"), "preview", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void ThePlugin_ReferencesNoNetworkingProtocolOrPersonaAssembly()
    {
        var path = RepositoryPaths.PluginAssembly();
        if (path is null)
        {
            return;
        }

        using var pe = new PEReader(File.OpenRead(path));
        var metadata = pe.GetMetadataReader();
        var references = metadata.AssemblyReferences
            .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))
            .ToList();

        Assert.NotEmpty(references);
        var forbidden = references.Where(name => ForbiddenAssemblyPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).ToList();
        Assert.True(forbidden.Count == 0, "The plugin references: " + string.Join(", ", forbidden));
    }

    [Fact]
    public void ThePlugin_UsesNoSocketOrHttpTypes()
    {
        var path = RepositoryPaths.PluginAssembly();
        if (path is null)
        {
            return;
        }

        using var pe = new PEReader(File.OpenRead(path));
        var metadata = pe.GetMetadataReader();
        var typeReferences = new List<string>();
        foreach (var handle in metadata.TypeReferences)
        {
            var reference = metadata.GetTypeReference(handle);
            typeReferences.Add(metadata.GetString(reference.Namespace) + "." + metadata.GetString(reference.Name));
        }

        var networking = typeReferences.Where(name => name.StartsWith("System.Net.", StringComparison.Ordinal)).ToList();
        Assert.True(networking.Count == 0, "The plugin uses: " + string.Join(", ", networking));
    }

    [Fact]
    public void ThePlayerBuild_HoldsNoProtocolOrPersonaCode_AndThePreviewFlavourDoes()
    {
        var path = RepositoryPaths.PluginAssembly();
        if (path is null)
        {
            return;
        }

        using var pe = new PEReader(File.OpenRead(path));
        var metadata = pe.GetMetadataReader();
        var namespaces = metadata.TypeDefinitions
            .Select(handle => metadata.GetString(metadata.GetTypeDefinition(handle).Namespace))
            .Distinct()
            .OrderBy(ns => ns, StringComparer.Ordinal)
            .ToList();
        var networking = namespaces.Where(ns => Within(ns, NetworkingNamespaces)).ToList();

        if (PreviewFlavour)
        {
            Assert.Contains("AetherFrame.Protocol", networking);
            Assert.Contains("AetherFrame.Personas", networking);
        }
        else
        {
            Assert.True(networking.Count == 0, "The player build holds: " + string.Join(", ", networking));
            var folders = namespaces.Where(ns => Within(ns, PluginNetworkingNamespaces)).ToList();
            Assert.True(folders.Count == 0, "The player build holds its networking folders' code: " + string.Join(", ", folders));
        }
    }

    [Fact]
    public void ThePlugin_LoadsNoNativeLibraryItself_AndDeclaresNoComImport()
    {
        // The other ways into native code that a P/Invoke declaration doesn't show: loading a
        // library and calling through a function pointer or a delegate made from one, or a COM
        // import. Neither flavour uses any of them.
        var path = RepositoryPaths.PluginAssembly();
        if (path is null)
        {
            return;
        }

        using var pe = new PEReader(File.OpenRead(path));
        var metadata = pe.GetMetadataReader();
        var found = new List<string>();
        foreach (var handle in metadata.TypeReferences)
        {
            var reference = metadata.GetTypeReference(handle);
            if (metadata.GetString(reference.Namespace) == "System.Runtime.InteropServices" && metadata.GetString(reference.Name) == "NativeLibrary")
            {
                found.Add("System.Runtime.InteropServices.NativeLibrary");
            }
        }

        foreach (var handle in metadata.MemberReferences)
        {
            var member = metadata.GetMemberReference(handle);
            var name = metadata.GetString(member.Name);
            if (name is not ("GetDelegateForFunctionPointer" or "GetFunctionPointerForDelegate") || member.Parent.Kind != HandleKind.TypeReference)
            {
                continue;
            }

            var parent = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
            if (metadata.GetString(parent.Namespace) == "System.Runtime.InteropServices" && metadata.GetString(parent.Name) == "Marshal")
            {
                found.Add("Marshal." + name);
            }
        }

        foreach (var handle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(handle);
            if ((type.Attributes & System.Reflection.TypeAttributes.Import) != 0)
            {
                found.Add("COM import " + metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name));
            }
        }

        // Every calli instruction names a stand-alone method signature; an unmanaged calling
        // convention there is a call through a native function pointer (delegate* unmanaged).
        for (var row = 1; row <= metadata.GetTableRowCount(TableIndex.StandAloneSig); row++)
        {
            var signature = metadata.GetStandaloneSignature(MetadataTokens.StandaloneSignatureHandle(row));
            if (signature.GetKind() != StandaloneSignatureKind.Method)
            {
                continue;
            }

            var header = metadata.GetBlobReader(signature.Signature).ReadSignatureHeader();
            if (header.CallingConvention is not (SignatureCallingConvention.Default or SignatureCallingConvention.VarArgs))
            {
                found.Add("an unmanaged calli signature (" + header.CallingConvention + ")");
            }
        }

        Assert.True(found.Count == 0, "The plugin reaches native code itself through: " + string.Join(", ", found));
    }

    private static bool Within(string ns, string[] roots) =>
        roots.Any(root => ns == root || ns.StartsWith(root + ".", StringComparison.Ordinal));

    [Fact]
    public void DeclaredNativeCalls_AreDpapisAlone_AndOnlyInThePreviewFlavour()
    {
        // The player build declares no P/Invoke of its own (it reaches native code only through
        // Dalamud and ImGui, like every plugin). The preview flavour declares exactly the three DPAPI
        // needs (docs/networking/DecisionRegister.md, K2), all in the DPAPI protector. This reads the
        // P/Invoke declarations, which LibraryImport's generated stubs also make.
        var path = RepositoryPaths.PluginAssembly();
        if (path is null)
        {
            return;
        }

        using var pe = new PEReader(File.OpenRead(path));
        var metadata = pe.GetMetadataReader();
        var imports = new List<string>();
        foreach (var handle in metadata.MethodDefinitions)
        {
            var method = metadata.GetMethodDefinition(handle);
            var import = method.GetImport();
            if (import.Module.IsNil)
            {
                continue;
            }

            var type = metadata.GetTypeDefinition(method.GetDeclaringType());
            while (type.GetDeclaringType() is { IsNil: false } outer)
            {
                type = metadata.GetTypeDefinition(outer);
            }

            var owner = metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name);
            imports.Add(owner + ": " + metadata.GetString(metadata.GetModuleReference(import.Module).Name) + "!" + metadata.GetString(import.Name));
        }

        imports.Sort(StringComparer.Ordinal);
        if (PreviewFlavour)
        {
            Assert.Equal(
                [
                    "AetherFrame.Services.Network.Personas.DpapiPersonaKeyProtector: crypt32.dll!CryptProtectData",
                    "AetherFrame.Services.Network.Personas.DpapiPersonaKeyProtector: crypt32.dll!CryptUnprotectData",
                    "AetherFrame.Services.Network.Personas.DpapiPersonaKeyProtector: kernel32.dll!LocalFree",
                ],
                imports);
        }
        else
        {
            Assert.True(imports.Count == 0, "The player build calls: " + string.Join(", ", imports));
        }
    }

    [Fact]
    public void ThePluginConfiguration_HasNoPersonaMembers()
    {
        var path = RepositoryPaths.PluginAssembly();
        if (path is null)
        {
            return;
        }

        using var pe = new PEReader(File.OpenRead(path));
        var metadata = pe.GetMetadataReader();
        var configuration = metadata.TypeDefinitions
            .Select(metadata.GetTypeDefinition)
            .Single(type => metadata.GetString(type.Namespace) == "AetherFrame" && metadata.GetString(type.Name) == "PluginConfiguration");

        var members = configuration.GetProperties().Select(handle => metadata.GetString(metadata.GetPropertyDefinition(handle).Name))
            .Concat(configuration.GetFields().Select(handle => metadata.GetString(metadata.GetFieldDefinition(handle).Name)))
            .ToList();
        Assert.NotEmpty(members);

        var forbidden = new[] { "Persona", "Network", "Key", "Publish", "Outbox", "Signer", "Backup", "Remote" };
        var offending = members.Where(name => forbidden.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase))).ToList();
        Assert.True(offending.Count == 0, "PluginConfiguration has: " + string.Join(", ", offending));
    }

    [Fact]
    public void LocalSources_NeverNameTheNetworkingCode()
    {
        // Only the networking folders (which a player build does not compile) and lines compiled
        // only into the preview flavour may name the protocol, the persona foundation or the
        // networking services. Everything local stays ignorant of them, whichever flavour is built.
        var offending = new List<string>();
        var scanned = 0;
        foreach (var (file, relative) in PluginSources())
        {
            if (NetworkingFolders.Any(folder => relative.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            scanned++;
            foreach (var lineNumber in PlayerBuildSourceScan.OffendingLines(File.ReadLines(file), NetworkingNames))
            {
                offending.Add($"{relative}:{lineNumber}");
            }
        }

        Assert.True(scanned > 0, "no plugin source was scanned");
        Assert.True(offending.Count == 0, "Local sources name the networking code at: " + string.Join(", ", offending));
    }

    [Fact]
    public void PluginSources_UseNoNetworkingApi()
    {
        var offending = new List<string>();
        var scanned = 0;
        foreach (var (file, relative) in PluginSources())
        {
            scanned++;
            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                if (NetworkingApis.Any(api => line.Contains(api, StringComparison.Ordinal)))
                {
                    offending.Add($"{relative}:{lineNumber}");
                }
            }
        }

        Assert.True(scanned > 0, "no plugin source was scanned");
        Assert.True(offending.Count == 0, "Plugin sources use a networking API at: " + string.Join(", ", offending));
    }

    /// <summary>Every C# source of the plugin project, with its path relative to the project folder.</summary>
    private static IEnumerable<(string File, string Relative)> PluginSources()
    {
        var project = Path.Combine(RepositoryPaths.Root().FullName, "AetherFrame");
        Assert.True(Directory.Exists(project), project);
        foreach (var file in Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(project, file);
            var top = relative.Split(Path.DirectorySeparatorChar, 2)[0];
            if (top is "obj" or "bin")
            {
                continue;
            }

            yield return (file, relative);
        }
    }
}

/// <summary>
/// Finds the lines of a plugin source that a player build compiles and that name something no
/// local source may. Lines inside <c>#if AETHERFRAME_NETWORK_PREVIEW</c>, and in the <c>#else</c>
/// of <c>#if !AETHERFRAME_NETWORK_PREVIEW</c>, are compiled only into the preview flavour and are
/// not scanned. The <c>#else</c> and <c>#elif</c> branches of a preview block, every other
/// conditional block, and a compound condition that merely mentions the symbol are scanned,
/// however deeply the blocks nest. A source that defines or undefines the symbol itself is
/// reported: only the build decides the flavour. Comment lines are not scanned.
/// </summary>
internal static class PlayerBuildSourceScan
{
    private const string Symbol = "AETHERFRAME_NETWORK_PREVIEW";

    internal static IReadOnlyList<int> OffendingLines(IEnumerable<string> lines, IReadOnlyList<string> names)
    {
        var offending = new List<int>();
        var blocks = new Stack<(bool Skipped, bool ElseSkipped)>();
        var lineNumber = 0;
        foreach (var line in lines)
        {
            lineNumber++;
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#'))
            {
                var directive = Directive(trimmed, out var condition);
                if (directive is "define" or "undef" && condition == Symbol)
                {
                    offending.Add(lineNumber);
                    continue;
                }

                if (directive == "if")
                {
                    blocks.Push((condition == Symbol, condition == "!" + Symbol));
                    continue;
                }

                if (directive == "elif" && blocks.TryPop(out var open))
                {
                    blocks.Push((condition == Symbol, open.ElseSkipped));
                    continue;
                }

                if (directive == "else" && blocks.TryPop(out var opened))
                {
                    blocks.Push((opened.ElseSkipped, opened.ElseSkipped));
                    continue;
                }

                if (directive == "endif")
                {
                    blocks.TryPop(out _);
                    continue;
                }
            }

            if (blocks.Any(block => block.Skipped) || trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (names.Any(name => line.Contains(name, StringComparison.Ordinal)))
            {
                offending.Add(lineNumber);
            }
        }

        return offending;
    }

    /// <summary>The name of a preprocessor directive, and its condition without any trailing comment.</summary>
    private static string Directive(string trimmed, out string condition)
    {
        var text = trimmed[1..].TrimStart();
        var comment = text.IndexOf("//", StringComparison.Ordinal);
        if (comment >= 0)
        {
            text = text[..comment];
        }

        var end = text.IndexOfAny([' ', '\t']);
        condition = end < 0 ? string.Empty : text[(end + 1)..].Trim();
        return end < 0 ? text.Trim() : text[..end];
    }
}
