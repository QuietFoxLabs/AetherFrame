using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// R3 as "Checking a character through the player's own connection" (the decision register)
/// amends it, on the compiled DLL: the Lodestone pipe's networking types are exactly the ones
/// recorded, and no type but the pipe names them; its socket and its WebSocket are made and used
/// only as the member rules say; and nothing on its non-private surface hands a stream, a socket
/// or a WebSocket out. The player flavour compiles no pipe at all.
/// </summary>
public partial class PluginAssemblyBoundaryTests
{
    private const string Pipe = "AetherFrame.Services.Network.Transport.LodestonePipe";
    private const string LodestoneHost = "na.finalfantasyxiv.com";
    private const int LodestonePort = 443;

    /// <summary>The socket's members the pipe may use: making it, <c>NoDelay</c>, connecting, the remote address, sending, receiving, <c>Shutdown</c> and <c>Dispose</c>, each by its exact parameters.</summary>
    private static readonly Dictionary<string, string[]> PipeSocketMembers = new(StringComparer.Ordinal)
    {
        [".ctor"] = ["System.Net.Sockets.SocketType", "System.Net.Sockets.ProtocolType"],
        ["set_NoDelay"] = ["Boolean"],
        ["ConnectAsync"] = ["System.Net.EndPoint", "System.Threading.CancellationToken"],
        ["get_RemoteEndPoint"] = [],
        ["SendAsync"] = ["System.ReadOnlyMemory`1<Byte>", "System.Net.Sockets.SocketFlags", "System.Threading.CancellationToken"],
        ["ReceiveAsync"] = ["System.Memory`1<Byte>", "System.Net.Sockets.SocketFlags", "System.Threading.CancellationToken"],
        ["Shutdown"] = ["System.Net.Sockets.SocketShutdown"],
        ["Dispose"] = [],
    };

    /// <summary>What the decision names as never used on the socket; the list above leaves each out, and this says so in the decision's words.</summary>
    private static readonly string[] NeverOnTheSocket = ["Bind", "Listen", "Accept", "AcceptAsync", "SendTo", "SendToAsync", "ReceiveFrom", "ReceiveFromAsync", "IOControl", "SetRawSocketOption", "DuplicateAndClose", "get_Handle", "get_SafeHandle"];

    /// <summary>
    /// What would reach a member the rules above never see: reflection, an
    /// <c>[UnsafeAccessor]</c>, <c>Unsafe</c> and the interop marshallers, a delegate made by name,
    /// expression trees and <c>dynamic</c>. The pipe names none of them, but for the marker the
    /// compiler puts on an <c>in</c> parameter. <c>System.Type</c> is named by the compiler's own
    /// attributes on async methods, so its members are held in the IL instead.
    /// </summary>
    private static readonly string[] WaysAroundTheMemberRules =
    [
        "System.Reflection.",
        "System.Runtime.InteropServices.",
        "System.Linq.Expressions.",
        "System.Dynamic.",
        "Microsoft.CSharp.",
        "System.Runtime.CompilerServices.UnsafeAccessorAttribute",
        "System.Runtime.CompilerServices.Unsafe",
        "System.Runtime.CompilerServices.RuntimeHelpers",
        "System.Activator",
        "System.Delegate",
        "System.AppDomain",
        "System.Runtime.Loader.",
    ];

    /// <summary>The WebSocket's members the pipe may use, on <c>WebSocket</c> or <c>ClientWebSocket</c>: the exchange's own, never one that makes a WebSocket over a stream.</summary>
    private static readonly string[] PipeWebSocketMembers = ["get_State", "SendAsync", "ReceiveAsync", "CloseAsync", "Abort", "Dispose"];

    /// <summary>
    /// The pipe's whole non-private surface, recorded from the compiled pipe: one call that runs the
    /// whole exchange (<c>RunAsync</c>), its limits and timeouts, the address check and the final
    /// message's reading, and the connection the exchange uses, which tests replace.
    /// </summary>
    private static readonly string[] PipeSurface =
    [
        "LodestonePipe",
        "LodestonePipe.MaxBytesToLodestone",
        "LodestonePipe.MaxBytesFromLodestone",
        "LodestonePipe.MaxMessageBytes",
        "LodestonePipe.MaxTextBytes",
        "LodestonePipe..ctor(AetherFrame.Protocol.Requests.DeploymentName,System.Net.Http.HttpMessageHandler,System.Version)",
        "LodestonePipe..ctor(AetherFrame.Protocol.Requests.DeploymentName,System.Net.Http.HttpMessageHandler,System.Version,System.Func`1<LodestonePipe+Link>)",
        "LodestonePipe.get_ExchangeTimeout()",
        "LodestonePipe.set_ExchangeTimeout(System.TimeSpan)",
        "LodestonePipe.get_ConnectTimeout()",
        "LodestonePipe.set_ConnectTimeout(System.TimeSpan)",
        "LodestonePipe.Allows(System.Net.IPAddress)",
        "LodestonePipe.RunAsync(AetherFrame.Protocol.Requests.RequestProofKind,Byte[],System.Threading.CancellationToken)",
        "LodestonePipe.ReadFinal(System.ReadOnlySpan`1<Byte>)",
        "LodestonePipe.Dispose()",
        "LodestonePipe+Link",
        "LodestonePipe+Link..ctor()",
        "LodestonePipe+Link.ConnectAsync(System.Threading.CancellationToken)",
        "LodestonePipe+Link.SendAsync(System.ReadOnlyMemory`1<Byte>,System.Threading.CancellationToken)",
        "LodestonePipe+Link.ReceiveAsync(System.Memory`1<Byte>,System.Threading.CancellationToken)",
        "LodestonePipe+Link.Dispose()",
    ];

    [Fact]
    public void ThePipesNetworkTypes_AreExactlyTheRecordedOnes_AndNamedOnlyByThePipe()
    {
        var path = RepositoryPaths.PluginAssembly();
        if (path is null)
        {
            return;
        }

        using var pe = new PEReader(File.OpenRead(path));
        var byType = NetworkTypeUse.ReferencesByType(pe);
        if (!PreviewFlavour)
        {
            Assert.False(byType.ContainsKey(Pipe), "the player flavour compiles no pipe");
            return;
        }

        // The list is exact: the pipe names every type on it, and no other networking type outside
        // R3's HTTP and its two named types.
        Assert.True(byType.TryGetValue(Pipe, out var pipeNames), "the sharing build compiles the pipe");
        var pipeNetworking = pipeNames!.Select(WithoutTypeArgumentPrefix)
            .Where(name => name.StartsWith("System.Net.", StringComparison.Ordinal) && !name.StartsWith("System.Net.Http.", StringComparison.Ordinal) && !PreviewNetworkTypes.Contains(name, StringComparer.Ordinal))
            .Where(name => !name.Contains('<', StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(PipeNetworkTypes.Order(StringComparer.Ordinal), pipeNetworking.Order(StringComparer.Ordinal));

        // No other type names one, its closures and state machines counted as it.
        var offending = new List<string>();
        foreach (var (type, references) in byType)
        {
            if (type == Pipe)
            {
                continue;
            }

            offending.AddRange(references.Select(WithoutTypeArgumentPrefix)
                .Where(name => PipeNetworkTypes.Any(pipeType => name == pipeType || name.Contains("<" + pipeType, StringComparison.Ordinal) || name.Contains("," + pipeType, StringComparison.Ordinal)))
                .Select(name => type + " names " + name));
        }

        Assert.True(offending.Count == 0, "R3 as amended: the pipe's types only in LodestonePipe. " + string.Join("; ", offending));

        // And every type the plugin takes from the pipe's assemblies is one of them; the pipe
        // references each of the three.
        var reader = pe.GetMetadataReader();
        var fromPipeAssemblies = new List<(string Assembly, string Type)>();
        foreach (var handle in reader.TypeReferences)
        {
            var reference = reader.GetTypeReference(handle);
            if (reference.ResolutionScope.Kind != HandleKind.AssemblyReference)
            {
                continue;
            }

            var assembly = reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope).Name);
            if (PipeNetworkAssemblies.Contains(assembly, StringComparer.OrdinalIgnoreCase))
            {
                fromPipeAssemblies.Add((assembly, reader.GetString(reference.Namespace) + "." + reader.GetString(reference.Name)));
            }
        }

        Assert.All(fromPipeAssemblies, used => Assert.Contains(used.Type, PipeNetworkTypes));
        Assert.Equal(PipeNetworkAssemblies.Order(StringComparer.Ordinal), fromPipeAssemblies.Select(used => used.Assembly).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ThePipesSocket_IsMadeConnectedAndUsedOnlyAsTheDecisionSays()
    {
        var path = RepositoryPaths.PluginAssembly();
        if (path is null)
        {
            return;
        }

        Assert.Empty(NeverOnTheSocket.Intersect(PipeSocketMembers.Keys));
        using var pe = new PEReader(File.OpenRead(path));
        var bodies = NetworkTypeUse.MethodBodies(pe, Pipe);
        if (!PreviewFlavour)
        {
            Assert.Empty(bodies);
            return;
        }

        var offending = new List<string>();
        var made = 0;
        var connected = 0;
        foreach (var (method, steps) in bodies)
        {
            for (var index = 0; index < steps.Count; index++)
            {
                var step = steps[index];
                var where = method + ": " + step;
                switch (step.Parent)
                {
                    case "System.Net.Sockets.Socket":
                        if (step.Name is null || !PipeSocketMembers.TryGetValue(step.Name, out var parameters) || !step.Parameters.SequenceEqual(parameters))
                        {
                            offending.Add(where + " (not a socket member the decision allows)");
                        }
                        else if (step.Name == ".ctor")
                        {
                            // new Socket(SocketType.Stream, ProtocolType.Tcp): Stream is 1, Tcp is 6.
                            made++;
                            if (step.OpCode != ILOpCode.Newobj || index < 2 || steps[index - 2].Constant != 1 || steps[index - 1].Constant != 6)
                            {
                                offending.Add(where + " (not new Socket(SocketType.Stream, ProtocolType.Tcp))");
                            }
                        }
                        else if (step.Name == "ConnectAsync")
                        {
                            // Connected only to the endpoint made just before it: between them nothing
                            // runs, nothing is stored or dropped, and only the token is loaded.
                            connected++;
                            var endpoint = LastCallBefore(steps, index);
                            if (endpoint < 0 || steps[endpoint] is not { OpCode: ILOpCode.Newobj, Parent: "System.Net.DnsEndPoint", Name: ".ctor" }
                                || index - endpoint - 1 > 2 || steps.GetRange(endpoint + 1, index - endpoint - 1).Any(between => !LoadsTheToken(between)))
                            {
                                offending.Add(where + " (not to the one DnsEndPoint made just before)");
                            }
                        }

                        break;
                    case "System.Net.DnsEndPoint":
                        // new DnsEndPoint("na.finalfantasyxiv.com", 443), the two constants as literals.
                        if (step.Name != ".ctor" || step.OpCode != ILOpCode.Newobj || !step.Parameters.SequenceEqual(["String", "Int32"])
                            || index < 2 || steps[index - 2].Text != LodestoneHost || steps[index - 1].Constant != LodestonePort)
                        {
                            offending.Add(where + " (not new DnsEndPoint(\"" + LodestoneHost + "\", " + LodestonePort + "))");
                        }

                        break;
                    case "System.Net.IPEndPoint":
                    case "System.Net.EndPoint":
                        if (step.Name != "get_Address")
                        {
                            offending.Add(where + " (an endpoint is only read for its address)");
                        }

                        break;
                }
            }
        }

        Assert.True(offending.Count == 0, "R3 as amended, the socket: " + string.Join("; ", offending));
        Assert.Equal((1, 1), (made, connected));
    }

    [Fact]
    public void ThePipesWebSocket_ConnectsOnlyThroughTheInvoker_AndSetsOnlyTheVersionHeader()
    {
        var path = RepositoryPaths.PluginAssembly();
        if (path is null)
        {
            return;
        }

        using var pe = new PEReader(File.OpenRead(path));

        // Nowhere in the plugin, the pipe included: a WebSocket made over a stream of its own.
        var overAStream = NetworkTypeUse.MemberReferences(pe.GetMetadataReader())
            .Where(member => member.Parent == "System.Net.WebSockets.WebSocket" && member.Name is "CreateFromStream" or "CreateClientWebSocket")
            .Select(member => member.Parent + "." + member.Name)
            .ToList();
        Assert.True(overAStream.Count == 0, "R3 as amended: " + string.Join(", ", overAStream));

        var bodies = NetworkTypeUse.MethodBodies(pe, Pipe);
        if (!PreviewFlavour)
        {
            Assert.Empty(bodies);
            return;
        }

        var offending = new List<string>();
        var connects = 0;
        var headers = 0;
        foreach (var (method, steps) in bodies)
        {
            for (var index = 0; index < steps.Count; index++)
            {
                var step = steps[index];
                var where = method + ": " + step;
                if (step.Parent == Pipe && step.Name == "invoker" && step.OpCode == ILOpCode.Stfld)
                {
                    // The invoker is made once, over the handler the pipe was given: SharingHandler's.
                    if (!method.EndsWith("..ctor", StringComparison.Ordinal) || index < 1
                        || steps[index - 1] is not { OpCode: ILOpCode.Newobj, Parent: "System.Net.Http.HttpMessageInvoker", Name: ".ctor" } made
                        || !made.Parameters.SequenceEqual(["System.Net.Http.HttpMessageHandler", "Boolean"]))
                    {
                        offending.Add(where + " (the invoker is set only in the constructor, over the handler given)");
                    }
                }

                switch (step.Parent)
                {
                    case "System.Net.WebSockets.ClientWebSocket" when step.Name == ".ctor":
                        if (step.Parameters.Length != 0)
                        {
                            offending.Add(where + " (not new ClientWebSocket())");
                        }

                        break;
                    case "System.Net.WebSockets.ClientWebSocket" when step.Name == "get_Options":
                        break;
                    case "System.Net.WebSockets.ClientWebSocket" when step.Name == "ConnectAsync":
                        // Only the overload that takes an invoker, given the pipe's own, never null:
                        // after the address, nothing runs but reading the deadline's token.
                        connects++;
                        var since = StepsSince(steps, index, candidate => candidate is { OpCode: ILOpCode.Newobj, Parent: "System.Uri", Name: ".ctor" });
                        if (!step.Parameters.SequenceEqual(["System.Uri", "System.Net.Http.HttpMessageInvoker", "System.Threading.CancellationToken"])
                            || since is null || since.Any(candidate => candidate.OpCode == ILOpCode.Ldnull || (candidate.Calls && candidate is not { Parent: "System.Threading.CancellationTokenSource", Name: "get_Token" }))
                            || !since.Any(candidate => candidate is { OpCode: ILOpCode.Ldfld, Parent: Pipe, Name: "invoker" }))
                        {
                            offending.Add(where + " (not ConnectAsync(uri, the pipe's invoker, token))");
                        }

                        break;
                    case "System.Net.WebSockets.ClientWebSocket" or "System.Net.WebSockets.WebSocket":
                        if (!PipeWebSocketMembers.Contains(step.Name, StringComparer.Ordinal))
                        {
                            offending.Add(where + " (not a WebSocket member the decision allows)");
                        }

                        break;
                    case "System.Net.WebSockets.ClientWebSocketOptions":
                        // Only R2's version header: no option .NET copies into its own handler, no
                        // compression, no HTTP version.
                        headers++;
                        var options = StepsSince(steps, index, candidate => candidate is { Parent: "System.Net.WebSockets.ClientWebSocket", Name: "get_Options" });
                        if (step.Name != "SetRequestHeader" || !step.Parameters.SequenceEqual(["String", "String"]) || options is null
                            || options.Any(candidate => candidate.Calls)
                            || options.Where(candidate => candidate.OpCode == ILOpCode.Ldstr).Select(candidate => candidate.Text).SingleOrDefault() != "User-Agent"
                            || !options.Any(candidate => candidate is { OpCode: ILOpCode.Ldfld, Parent: Pipe, Name: "versionHeader" }))
                        {
                            offending.Add(where + " (not SetRequestHeader(\"User-Agent\", the version header))");
                        }

                        break;
                }
            }
        }

        Assert.True(offending.Count == 0, "R3 as amended, the WebSocket: " + string.Join("; ", offending));
        Assert.Equal((1, 1), (connects, headers));
    }

    [Fact]
    public void ThePipe_HasNoWayAroundItsMemberRules()
    {
        var path = RepositoryPaths.PluginAssembly();
        if (path is null)
        {
            return;
        }

        using var pe = new PEReader(File.OpenRead(path));
        var byType = NetworkTypeUse.ReferencesByType(pe);
        if (!PreviewFlavour)
        {
            Assert.False(byType.ContainsKey(Pipe), "the player flavour compiles no pipe");
            return;
        }

        var around = byType[Pipe].Select(WithoutTypeArgumentPrefix)
            .Where(name => name != "System.Runtime.InteropServices.InAttribute"
                && WaysAroundTheMemberRules.Any(way => way.EndsWith('.') ? name.StartsWith(way, StringComparison.Ordinal) : name == way || name.StartsWith(way + "`", StringComparison.Ordinal)))
            .ToList();
        around.AddRange(NetworkTypeUse.MethodBodies(pe, Pipe)
            .SelectMany(body => body.Steps.Where(step => step.Parent == "System.Type" && step.Name != "GetTypeFromHandle").Select(step => body.Method + ": " + step)));
        Assert.True(around.Count == 0, "R3 as amended: the pipe names " + string.Join(", ", around));
        var bodiless = NetworkTypeUse.BodilessMethods(pe, Pipe);
        Assert.True(bodiless.Count == 0, "R3 as amended: the pipe declares methods with no body: " + string.Join(", ", bodiless));
    }

    [Fact]
    public void NothingLeaksOutOfThePipe()
    {
        var path = RepositoryPaths.PluginAssembly();
        if (path is null)
        {
            return;
        }

        using var pe = new PEReader(File.OpenRead(path));
        var surface = NetworkTypeUse.NonPrivateSurface(pe, Pipe);
        if (!PreviewFlavour)
        {
            Assert.Empty(surface);
            return;
        }

        // No stream, socket or WebSocket, nor a delegate, task or anything else generic over one.
        var leaking = surface
            .SelectMany(member => member.Names.Select(WithoutTypeArgumentPrefix).Where(Leaks).Select(name => member.Member + " names " + name))
            .ToList();
        Assert.True(leaking.Count == 0, "R3 as amended, nothing leaks out: " + string.Join("; ", leaking));

        // And the surface is the recorded one: anything added to it is a deliberate change here.
        var prefix = Pipe[..(Pipe.LastIndexOf('.') + 1)];
        Assert.Equal(PipeSurface.Order(StringComparer.Ordinal), surface.Select(member => member.Member.Replace(prefix, "", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
    }

    private static string WithoutTypeArgumentPrefix(string name) =>
        name.StartsWith(NetworkTypeUse.TypeArgumentPrefix, StringComparison.Ordinal) ? name[NetworkTypeUse.TypeArgumentPrefix.Length..] : name;

    /// <summary>A stream, a socket, a WebSocket or TLS, in any of their namespaces; R3's address family is an enum, and stays allowed.</summary>
    private static bool Leaks(string name) =>
        name == "System.IO.Stream" || name.StartsWith("System.IO.Pipelines.", StringComparison.Ordinal)
        || (name.StartsWith("System.Net.Sockets.", StringComparison.Ordinal) && name != "System.Net.Sockets.AddressFamily")
        || name.StartsWith("System.Net.WebSockets.", StringComparison.Ordinal) || name.StartsWith("System.Net.Security.", StringComparison.Ordinal);

    /// <summary>The index of the last step before <paramref name="index"/> that runs code, or -1.</summary>
    private static int LastCallBefore(List<IlStep> steps, int index)
    {
        for (var at = index - 1; at >= 0; at--)
        {
            if (steps[at].Calls)
            {
                return at;
            }
        }

        return -1;
    }

    /// <summary>A step that only loads an argument or a field: how the token reaches a call in a method or its state machine.</summary>
    private static bool LoadsTheToken(IlStep step) =>
        step.OpCode is ILOpCode.Ldarg_0 or ILOpCode.Ldarg_1 or ILOpCode.Ldarg_2 or ILOpCode.Ldarg_3 or ILOpCode.Ldarg_s or ILOpCode.Ldarg or ILOpCode.Ldfld;

    /// <summary>The steps strictly between the last one before <paramref name="index"/> that <paramref name="start"/> matches and <paramref name="index"/>; null when none matches.</summary>
    private static List<IlStep>? StepsSince(List<IlStep> steps, int index, Func<IlStep, bool> start)
    {
        for (var at = index - 1; at >= 0; at--)
        {
            if (start(steps[at]))
            {
                return steps.GetRange(at + 1, index - at - 1);
            }
        }

        return null;
    }
}
