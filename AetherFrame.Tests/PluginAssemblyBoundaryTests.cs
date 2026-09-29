using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The built plugin reaches nothing online and nothing experimental: it references no networking
/// assembly and neither the remote protocol nor the persona foundation. The tutorial, the Help
/// menu and the redesign added a good deal of interface code; none of it may have pulled any of
/// those in. Checked against the DLL that ships (CI names it in AETHERFRAME_PLUGIN_ASSEMBLY),
/// or the local build output when there is one.
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
}
