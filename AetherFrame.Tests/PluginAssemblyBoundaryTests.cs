using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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

    /// <summary>Networking APIs no plugin source outside <c>Services/Network</c> may use, in any flavour.</summary>
    private static readonly string[] NetworkingApis = ["HttpClient", "HttpMessageHandler", "HttpMessageInvoker", "SocketsHttpHandler", "DelegatingHandler", "HappyEyeballs", "WebRequest", "WebClient", "System.Net.", "OpenLink", "Dns."];

    /// <summary>
    /// Networking APIs no plugin source may use at all, <c>Services/Network</c> included (decision
    /// R3): raw sockets, TLS streams, WebSockets, QUIC, DNS, the old request types, a listener, mail,
    /// and Dalamud's link opener. <c>Sockets</c> is matched as a whole name, so R3's
    /// <c>SocketsHttpHandler</c> passes. Every way of overriding certificate validation is named
    /// too: the type check refuses them already, as each references <c>SslPolicyErrors</c>. So are
    /// starting a process (a link opened in a browser leaves as surely as a request), and a
    /// networking type named in a string, which reflection could load past the type check.
    /// </summary>
    private static readonly string[] RefusedEverywhere = ["WebRequest", "WebClient", "OpenLink", "Dns.", "SslStream", "WebSocket", "System.Net.Quic", "QuicConnection", "QuicListener", "HttpListener", "System.Net.Mail", "System.Net.Security", "TcpClient", "UdpClient", "NetworkStream", "SocketException", "SocketError", "HappyHttpClient", "ServerCertificateCustomValidationCallback", "DangerousAcceptAnyServerCertificateValidator", "RemoteCertificateValidationCallback", "SslOptions", "Process.Start", "ProcessStartInfo", "ShellExecute", "\"System.Net", "\"Dalamud.Networking"];

    /// <summary>
    /// What the handler must never be told (decision R2): to send Windows or proxy credentials or a
    /// client certificate, to accept any certificate or change how TLS is set up, to decompress, to
    /// ask for another HTTP version, or to use another proxy or a cookie store. Refused by name, as
    /// member references in the compiled DLL, whatever the source looked like.
    /// </summary>
    private static readonly string[] RefusedHttpMembers =
    [
        "set_UseDefaultCredentials", "set_Credentials", "set_DefaultProxyCredentials", "set_PreAuthenticate",
        "set_ClientCertificateOptions", "get_ClientCertificates", "set_ServerCertificateCustomValidationCallback",
        "get_DangerousAcceptAnyServerCertificateValidator", "get_SslOptions", "set_SslOptions", "set_AutomaticDecompression",
        "set_DefaultRequestVersion", "set_DefaultVersionPolicy", "set_Version", "set_VersionPolicy", "set_Proxy",
        "set_CookieContainer", "set_MaxAutomaticRedirections",
    ];

    /// <summary>The line breaks C# reads that <see cref="File.ReadLines(string)"/> doesn't, which could hide code on a line that starts as a comment.</summary>
    private static readonly char[] HiddenLineBreaks = [(char)0x85, (char)0x2028, (char)0x2029];

    /// <summary>
    /// The only assemblies of <c>System.Net</c> the preview flavour may reference (decision R3):
    /// HTTP, and the primitives that hold <c>HttpStatusCode</c> and the connect callback's
    /// <c>AddressFamily</c>. The player build references none.
    /// </summary>
    private static readonly string[] PreviewNetworkAssemblies = ["System.Net.Http", "System.Net.Primitives"];

    /// <summary>The only <c>System.Net</c> types outside HTTP's namespaces the preview flavour may use (decision R3).</summary>
    private static readonly string[] PreviewNetworkTypes = ["System.Net.HttpStatusCode", "System.Net.Sockets.AddressFamily"];

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
        var forbidden = references.Where(name => ForbiddenAssemblyPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            || name.Equals("System.Net", StringComparison.OrdinalIgnoreCase) || name.StartsWith("System.Net.", StringComparison.OrdinalIgnoreCase)).ToList();

        // Decision R3: the preview flavour may reference HTTP and the network primitives, nothing else.
        if (PreviewFlavour)
        {
            forbidden.RemoveAll(name => PreviewNetworkAssemblies.Contains(name, StringComparer.OrdinalIgnoreCase));
        }
        else
        {
            forbidden.AddRange(references.Where(name => PreviewNetworkAssemblies.Contains(name, StringComparer.OrdinalIgnoreCase) && !forbidden.Contains(name)));
        }

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
        var dalamudNetworking = typeReferences.Where(name => name.StartsWith("Dalamud.Networking.", StringComparison.Ordinal)).ToList();
        if (PreviewFlavour)
        {
            // Decision R3's exact allowlist: every type in System.Net.Http and its Headers, the two
            // named types, and Dalamud's HappyEyeballsCallback.
            networking.RemoveAll(name => IsInNamespace(name, "System.Net.Http") || IsInNamespace(name, "System.Net.Http.Headers") || PreviewNetworkTypes.Contains(name, StringComparer.Ordinal));
            dalamudNetworking.RemoveAll(name => name == "Dalamud.Networking.Http.HappyEyeballsCallback");
        }

        Assert.True(networking.Count == 0, "The plugin uses: " + string.Join(", ", networking));
        Assert.True(dalamudNetworking.Count == 0, "The plugin uses Dalamud's networking: " + string.Join(", ", dalamudNetworking));
    }

    [Fact]
    public void NetworkingTypes_AreNamedOnlyInsideServicesNetwork()
    {
        // Decision R3: in the compiled DLL, whatever the sources looked like, no type outside
        // AetherFrame.Services.Network names a networking type anywhere: signatures, attributes, base
        // types or IL. The player build names none at all (above).
        var path = RepositoryPaths.PluginAssembly();
        if (path is null)
        {
            return;
        }

        using var pe = new PEReader(File.OpenRead(path));
        var offending = new List<string>();
        foreach (var (type, references) in NetworkTypeUse.ReferencesByType(pe))
        {
            if (type.StartsWith("AetherFrame.Services.Network.", StringComparison.Ordinal))
            {
                continue;
            }

            offending.AddRange(references
                .Where(name => name.StartsWith("System.Net.", StringComparison.Ordinal) || name.StartsWith("Dalamud.Networking.", StringComparison.Ordinal))
                .Select(name => type + " names " + name));
        }

        Assert.True(offending.Count == 0, "R3: networking only under Services/Network. " + string.Join("; ", offending));
    }

    [Fact]
    public void ThePlugin_TellsHttpNothingR2RulesOut()
    {
        var path = RepositoryPaths.PluginAssembly();
        if (path is null)
        {
            return;
        }

        using var pe = new PEReader(File.OpenRead(path));
        var offending = NetworkTypeUse.MemberReferences(pe.GetMetadataReader())
            .Where(member => member.Parent.StartsWith("System.Net.Http.", StringComparison.Ordinal) && RefusedHttpMembers.Contains(member.Name, StringComparer.Ordinal))
            .Select(member => member.Parent + "." + member.Name)
            .ToList();
        Assert.True(offending.Count == 0, "R2: " + string.Join(", ", offending));
    }

    [Fact]
    public void ThePlugin_BuildsItsHttpStackOnlyInSharingHandler()
    {
        // R2 and R3, on the compiled DLL: the one handler is SharingHandler's, made through
        // Dalamud's connect callback; no handler or client with the defaults (which follow
        // redirects, keep cookies and connect on their own) is made anywhere; and nothing turns
        // redirects or cookies back on.
        var path = RepositoryPaths.PluginAssembly();
        if (path is null)
        {
            return;
        }

        using var pe = new PEReader(File.OpenRead(path));
        var offending = new List<string>();
        foreach (var use in NetworkTypeUse.MemberUses(pe))
        {
            // Any use of a constructor counts, a subclass's base() call as much as a new: the two
            // unsealed types would otherwise be made with their defaults through a class of our own.
            var where = use.Type + ": " + use.Parent + "." + use.Name;
            if (use.Name == ".ctor")
            {
                if (use.Parent == "System.Net.Http.SocketsHttpHandler" && use.Type != "AetherFrame.Services.Network.Transport.SharingHandler")
                {
                    offending.Add(where + " (a handler outside SharingHandler)");
                }

                if (use.Parent == "System.Net.Http.HttpClientHandler" || (use.Parent == "System.Net.Http.HttpClient" && use.Parameters == 0))
                {
                    offending.Add(where + " (a handler with the defaults)");
                }
            }

            if (use.Name is "set_AllowAutoRedirect" or "set_UseCookies" && use.Parent.StartsWith("System.Net.Http.", StringComparison.Ordinal) && use.Previous != ILOpCode.Ldc_i4_0)
            {
                offending.Add(where + " (set to anything but false)");
            }
        }

        Assert.True(offending.Count == 0, "R2: " + string.Join("; ", offending));
    }

    [Fact]
    public void ThePlugin_StartsNoProcessAndOpensNoLink()
    {
        // A link opened in a browser, or a program started with one, leaves as surely as a request.
        var path = RepositoryPaths.PluginAssembly();
        if (path is null)
        {
            return;
        }

        using var pe = new PEReader(File.OpenRead(path));
        var offending = NetworkTypeUse.ReferencesByType(pe)
            .SelectMany(type => type.Value.Where(name => name is "System.Diagnostics.Process" or "System.Diagnostics.ProcessStartInfo").Select(name => type.Key + " names " + name))
            .Concat(NetworkTypeUse.MemberUses(pe).Where(use => use.Name == "OpenLink" && use.Parent.StartsWith("Dalamud.", StringComparison.Ordinal)).Select(use => use.Type + " calls " + use.Parent + ".OpenLink"))
            .ToList();
        Assert.True(offending.Count == 0, string.Join("; ", offending));
    }

    [Fact]
    public void TheNetworkNamespace_IsDeclaredOnlyInTheNetworkFolder()
    {
        // The compiled check above exempts AetherFrame.Services.Network by namespace, and R3 is
        // about the folder. So every type a plugin source declares, read as the compiler reads it
        // (nested namespaces, whitespace, comments, escapes and verbatim names included), in the
        // player flavour and in the preview flavour, is in that namespace exactly when its file is
        // under Services/Network.
        var networkFolder = Path.Combine("Services", "Network") + Path.DirectorySeparatorChar;
        var offending = new List<string>();
        var types = 0;
        foreach (var (file, relative) in PluginSources())
        {
            var inside = relative.StartsWith(networkFolder, StringComparison.OrdinalIgnoreCase);
            var text = File.ReadAllText(file);
            foreach (var symbols in new[] { Array.Empty<string>(), ["AETHERFRAME_NETWORK_PREVIEW"] })
            {
                var tree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Preview, preprocessorSymbols: symbols));
                foreach (var declaration in tree.GetRoot().DescendantNodes().Where(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax))
                {
                    types++;
                    var ns = string.Join(".", declaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(namespaceDeclaration => NameOf(namespaceDeclaration.Name)));
                    var network = ns == "AetherFrame.Services.Network" || ns.StartsWith("AetherFrame.Services.Network.", StringComparison.Ordinal);
                    if (network != inside)
                    {
                        offending.Add(relative + " (" + (ns.Length == 0 ? "no namespace" : ns) + ")");
                    }
                }
            }
        }

        Assert.True(types > 0, "no type was read");
        Assert.True(offending.Count == 0, "R3: the network namespace belongs to Services/Network alone: " + string.Join(", ", offending.Distinct()));
    }

    /// <summary>A namespace's name as the compiler binds it: escapes undone, <c>@</c> dropped.</summary>
    private static string NameOf(NameSyntax name) => name switch
    {
        QualifiedNameSyntax qualified => NameOf(qualified.Left) + "." + NameOf(qualified.Right),
        AliasQualifiedNameSyntax aliased => NameOf(aliased.Name),
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        _ => name.ToString(),
    };

    [Fact]
    public void TheSharingHandler_FollowsNoRedirectAndKeepsNoCookie()
    {
        // R2: SharingHandler's constructor sets AllowAutoRedirect and UseCookies to false, read
        // from the compiled IL: each setter is called right after the constant 0 is loaded.
        var path = RepositoryPaths.PluginAssembly();
        if (path is null)
        {
            return;
        }

        using var pe = new PEReader(File.OpenRead(path));
        var metadata = pe.GetMetadataReader();
        var handler = metadata.TypeDefinitions
            .Select(metadata.GetTypeDefinition)
            .Where(type => metadata.GetString(type.Namespace) == "AetherFrame.Services.Network.Transport" && metadata.GetString(type.Name) == "SharingHandler")
            .ToList();
        if (handler.Count == 0)
        {
            Assert.False(PreviewFlavour, "The preview flavour holds SharingHandler.");
            return;
        }

        var constructor = metadata.GetMethodDefinition(Assert.Single(handler[0].GetMethods(), handle => metadata.GetString(metadata.GetMethodDefinition(handle).Name) == ".ctor"));
        var instructions = IlScan.Instructions(pe.GetMethodBody(constructor.RelativeVirtualAddress).GetILReader());
        var setToFalse = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 1; index < instructions.Count; index++)
        {
            var (opCode, token) = instructions[index];
            if ((opCode == ILOpCode.Callvirt || opCode == ILOpCode.Call) && MetadataTokens.Handle(token).Kind == HandleKind.MemberReference
                && instructions[index - 1].OpCode == ILOpCode.Ldc_i4_0)
            {
                setToFalse.Add(metadata.GetString(metadata.GetMemberReference((MemberReferenceHandle)MetadataTokens.Handle(token)).Name));
            }
        }

        Assert.Contains("set_AllowAutoRedirect", setToFalse);
        Assert.Contains("set_UseCookies", setToFalse);

        // And it connects through Dalamud's dual-stack callback, never on its own.
        var connect = false;
        for (var index = 1; index < instructions.Count; index++)
        {
            var (opCode, token) = instructions[index];
            if (opCode == ILOpCode.Ldftn && MetadataTokens.Handle(token).Kind == HandleKind.MemberReference)
            {
                var member = metadata.GetMemberReference((MemberReferenceHandle)MetadataTokens.Handle(token));
                connect |= metadata.GetString(member.Name) == "ConnectCallback" && member.Parent.Kind == HandleKind.TypeReference
                    && metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)member.Parent).Name) == "HappyEyeballsCallback";
            }
        }

        Assert.True(connect, "SharingHandler connects through HappyEyeballsCallback.ConnectCallback.");
    }

    [Fact]
    public void PluginSources_HoldNoLineBreakTheScansCantSee()
    {
        // C# ends a line at U+0085, U+2028 and U+2029 too; File.ReadLines doesn't, so code after one
        // on a line that starts with // would compile unscanned.
        var offending = new List<string>();
        foreach (var (file, relative) in PluginSources())
        {
            if (File.ReadAllText(file).IndexOfAny(HiddenLineBreaks) >= 0)
            {
                offending.Add(relative);
            }
        }

        Assert.True(offending.Count == 0, "Plugin sources hold a line break only the compiler reads: " + string.Join(", ", offending));
    }

    /// <summary>Whether <paramref name="type"/> (namespace and name) is declared directly in <paramref name="ns"/>.</summary>
    private static bool IsInNamespace(string type, string ns) =>
        type.StartsWith(ns + ".", StringComparison.Ordinal) && type.IndexOf('.', ns.Length + 1) < 0;

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
    public void DeclaredNativeCalls_AreDpapisAndTheWrittenThroughMove_AndOnlyInThePreviewFlavour()
    {
        // The player build declares no P/Invoke of its own (it reaches native code only through
        // Dalamud and ImGui, like every plugin). The preview flavour declares exactly the three DPAPI
        // needs, all in the DPAPI protector (docs/networking/DecisionRegister.md, K2), and the
        // written-through move the persona files need, in its own class (P3). This reads the
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
                    "AetherFrame.Services.Network.Personas.WrittenThroughMove: kernel32.dll!MoveFileExW",
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
        // Decision R3: networking only under Services/Network, the preview flavour's; and even there
        // nothing but HTTP through Dalamud's connect callback.
        var networkFolder = Path.Combine("Services", "Network") + Path.DirectorySeparatorChar;
        var offending = new List<string>();
        var scanned = 0;
        foreach (var (file, relative) in PluginSources())
        {
            scanned++;
            var inNetworkFolder = relative.StartsWith(networkFolder, StringComparison.OrdinalIgnoreCase);
            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                // A global using would carry a networking namespace into every file unseen.
                var globalUsing = line.Contains("global using", StringComparison.Ordinal)
                    && (line.Contains("System.Net", StringComparison.Ordinal) || line.Contains("Dalamud.Networking", StringComparison.Ordinal));
                var refused = RefusedEverywhere.Any(api => line.Contains(api, StringComparison.Ordinal)) || NamesSockets(line) || globalUsing;
                var outsideTheFolder = !inNetworkFolder && NetworkingApis.Any(api => line.Contains(api, StringComparison.Ordinal));
                if (refused || outsideTheFolder)
                {
                    offending.Add($"{relative}:{lineNumber}");
                }
            }
        }

        Assert.True(scanned > 0, "no plugin source was scanned");
        Assert.True(offending.Count == 0, "Plugin sources use a networking API at: " + string.Join(", ", offending));
    }

    /// <summary>
    /// Whether <paramref name="line"/> names <c>Sockets</c> as a whole name (the namespace, a
    /// <c>Socket</c> type), as opposed to part of R3's <c>SocketsHttpHandler</c> or
    /// <c>SocketsHttpConnectionContext</c>.
    /// </summary>
    internal static bool NamesSockets(string line) => System.Text.RegularExpressions.Regex.IsMatch(line, @"\bSockets?\b");

    /// <summary>
    /// Calls no plugin source may make, in any flavour, each ruled out by a decision in
    /// docs/networking/DecisionRegister.md: the call, the one file allowed to name it (where it is
    /// defined), and the rule.
    /// </summary>
    public static TheoryData<string, string, string> CallsTheDecisionsRuleOut => new()
    {
        { "TryOpenActiveSigner", "", "L10: every signer the plugin opens is bound to the persona an operation showed, through TryOpenSigner" },
        { "RunWithDpapiClaim", "PersonaCapabilityProbe.cs", "K3: the plugin calls only the probe's public entry point, which binds the protection claim" },
        { "new PersonaManager(", "", "P3: the plugin makes its manager with PersonaManager.Load, never without its registry" },
    };

    [Theory]
    [MemberData(nameof(CallsTheDecisionsRuleOut))]
    public void PluginSources_NeverMakeACallTheDecisionsRuleOut(string call, string definedIn, string rule)
    {
        var offending = new List<string>();
        var scanned = 0;
        foreach (var (file, relative) in PluginSources())
        {
            scanned++;
            if (definedIn.Length > 0 && string.Equals(Path.GetFileName(file), definedIn, StringComparison.Ordinal))
            {
                continue;
            }

            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                if (!line.TrimStart().StartsWith("//", StringComparison.Ordinal) && line.Contains(call, StringComparison.Ordinal))
                {
                    offending.Add($"{relative}:{lineNumber}");
                }
            }
        }

        Assert.True(scanned > 0, "no plugin source was scanned");
        Assert.True(offending.Count == 0, rule + ". Found at: " + string.Join(", ", offending));
    }

    [Fact]
    public void ThePreviewFlavour_MakesItsPersonaManagerOnlyWithItsRegistry()
    {
        // P3: the plugin makes its manager with PersonaManager.Load and never falls back to the
        // registry-less public constructor, which is for tests. A source scan can't see every way
        // C# writes a constructor call (a target-typed new, a field initializer), so this reads the
        // compiled IL: nothing outside PersonaManager itself constructs one through that
        // constructor. A player build holds no PersonaManager, so there is nothing to check there.
        var path = RepositoryPaths.PluginAssembly();
        if (path is null)
        {
            return;
        }

        using var pe = new PEReader(File.OpenRead(path));
        var metadata = pe.GetMetadataReader();
        var manager = metadata.TypeDefinitions
            .Select(handle => (Handle: handle, Type: metadata.GetTypeDefinition(handle)))
            .Where(t => metadata.GetString(t.Type.Namespace) == "AetherFrame.Personas" && metadata.GetString(t.Type.Name) == "PersonaManager")
            .Select(t => (TypeDefinitionHandle?)t.Handle)
            .SingleOrDefault();
        if (manager is null)
        {
            Assert.False(PreviewFlavour, "The preview flavour holds no PersonaManager.");
            return;
        }

        var publicConstructors = metadata.GetTypeDefinition(manager.Value).GetMethods()
            .Where(handle =>
            {
                var method = metadata.GetMethodDefinition(handle);
                return metadata.GetString(method.Name) == ".ctor" && (method.Attributes & System.Reflection.MethodAttributes.MemberAccessMask) == System.Reflection.MethodAttributes.Public;
            })
            .Select(handle => MetadataTokens.GetToken(handle))
            .ToHashSet();
        Assert.NotEmpty(publicConstructors);

        var offending = new List<string>();
        foreach (var handle in metadata.MethodDefinitions)
        {
            var method = metadata.GetMethodDefinition(handle);
            if (method.RelativeVirtualAddress == 0 || method.GetDeclaringType() == manager.Value)
            {
                continue;
            }

            var body = pe.GetMethodBody(method.RelativeVirtualAddress);
            foreach (var (opCode, token) in IlScan.TokenOperands(body.GetILReader()))
            {
                if (opCode == ILOpCode.Newobj && publicConstructors.Contains(token))
                {
                    var type = metadata.GetTypeDefinition(method.GetDeclaringType());
                    offending.Add(metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name) + "." + metadata.GetString(method.Name));
                }
            }
        }

        Assert.True(offending.Count == 0, "P3: the plugin makes its PersonaManager with PersonaManager.Load, never without its registry. Constructed in: " + string.Join(", ", offending));
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
