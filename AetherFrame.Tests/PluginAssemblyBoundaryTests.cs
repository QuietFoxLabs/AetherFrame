using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
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
        var networking = metadata.TypeDefinitions
            .Select(handle => metadata.GetString(metadata.GetTypeDefinition(handle).Namespace))
            .Where(ns => NetworkingNamespaces.Any(n => ns == n || ns.StartsWith(n + ".", StringComparison.Ordinal)))
            .Distinct()
            .OrderBy(ns => ns, StringComparer.Ordinal)
            .ToList();

        if (PreviewFlavour)
        {
            Assert.Contains("AetherFrame.Protocol", networking);
            Assert.Contains("AetherFrame.Personas", networking);
        }
        else
        {
            Assert.True(networking.Count == 0, "The player build holds: " + string.Join(", ", networking));
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
        // Only the networking folders, and lines compiled only into the preview flavour, may name
        // the protocol, the persona foundation or the networking services. Everything local stays
        // ignorant of them, whichever flavour is built.
        var offending = new List<string>();
        foreach (var (file, relative) in PluginSources())
        {
            if (NetworkingFolders.Any(folder => relative.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var previewDepth = 0;
            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("#if AETHERFRAME_NETWORK_PREVIEW", StringComparison.Ordinal))
                {
                    previewDepth = 1;
                    continue;
                }

                if (previewDepth > 0)
                {
                    if (trimmed.StartsWith("#if", StringComparison.Ordinal))
                    {
                        previewDepth++;
                    }
                    else if (trimmed.StartsWith("#endif", StringComparison.Ordinal))
                    {
                        previewDepth--;
                    }

                    continue;
                }

                if (trimmed.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                if (NetworkingNames.Any(name => line.Contains(name, StringComparison.Ordinal)))
                {
                    offending.Add($"{relative}:{lineNumber}");
                }
            }
        }

        Assert.True(offending.Count == 0, "Local sources name the networking code at: " + string.Join(", ", offending));
    }

    [Fact]
    public void PluginSources_UseNoNetworkingApi()
    {
        var offending = new List<string>();
        foreach (var (file, relative) in PluginSources())
        {
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
