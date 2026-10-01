using System;
using System.Reflection;

namespace AetherFrame.Services.Diagnostics;

/// <summary>
/// Which AetherFrame build is running, read from the assembly's own metadata: nothing here is
/// maintained by hand. The product version is set once in Version.props; the SDK writes it into
/// <see cref="AssemblyInformationalVersionAttribute"/>, appending "+&lt;commit&gt;" when the build
/// had Git source information. That commit is the checked-out one, so a build with uncommitted
/// changes reports the commit it started from.
/// </summary>
internal sealed record AetherFrameBuildInfo(string Version, string? Revision)
{
    /// <summary>Length a full commit id is shortened to, as Git abbreviates it.</summary>
    internal const int ShortRevisionLength = 7;

    /// <summary>The build this code was compiled into.</summary>
    internal static AetherFrameBuildInfo Current { get; } = FromAssembly(typeof(AetherFrameBuildInfo).Assembly);

    internal static AetherFrameBuildInfo FromAssembly(Assembly assembly) =>
        Parse(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3));

    /// <summary>
    /// "0.1.0+1bf26e1b01a3…" is version 0.1.0, build 1bf26e1. Build metadata that isn't a commit
    /// id is kept as written; without any, there is no revision.
    /// </summary>
    internal static AetherFrameBuildInfo Parse(string? informationalVersion)
    {
        var value = informationalVersion?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return new AetherFrameBuildInfo("unknown", null);
        }

        var plus = value.IndexOf('+');
        if (plus < 0)
        {
            return new AetherFrameBuildInfo(value, null);
        }

        var version = value[..plus];
        var metadata = value[(plus + 1)..];
        if (metadata.Length == 0)
        {
            return new AetherFrameBuildInfo(version, null);
        }

        return new AetherFrameBuildInfo(version, IsCommitId(metadata) ? metadata[..ShortRevisionLength] : metadata);
    }

    /// <summary>
    /// Whether this is the networking preview flavour (docs/networking/DecisionRegister.md, P2): the
    /// build that compiles the protocol and persona sources in, and later the preview commands. A
    /// player build, and every official build, is never the preview flavour, and nothing in one can
    /// create a persona key or a signed document.
    /// </summary>
    internal static bool NetworkPreview =>
#if AETHERFRAME_NETWORK_PREVIEW
        true;
#else
        false;
#endif

    /// <summary>The version as a number, without a pre-release suffix: 0.0.0 when it can't be read.</summary>
    internal Version ProductVersion
    {
        get
        {
            var core = Version.Split('-', 2)[0];
            return System.Version.TryParse(core, out var parsed) ? parsed : new Version(0, 0, 0);
        }
    }

    /// <summary>"AetherFrame 0.1.0".</summary>
    internal string DisplayName => $"AetherFrame {Version}";

    /// <summary>"AetherFrame 0.1.0 (build 1bf26e1)", or just the display name without a revision; the sharing build adds "[sharing]".</summary>
    internal string Describe()
    {
        var text = Revision is null ? DisplayName : $"{DisplayName} (build {Revision})";
        return NetworkPreview ? text + " [sharing]" : text;
    }

    private static bool IsCommitId(string text)
    {
        if (text.Length < ShortRevisionLength)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
