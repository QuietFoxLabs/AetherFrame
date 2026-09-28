using System.Collections.Generic;
using System.Linq;

namespace AetherFrame.ReleaseTools;

public enum PublicationChange
{
    /// <summary>The channel already serves the version; nothing is written.</summary>
    Unchanged,

    /// <summary>A channel gets its first version or a newer one.</summary>
    Publish,

    /// <summary>A channel is deliberately moved to an older version.</summary>
    Rollback,
}

/// <summary>
/// What one request does to the repository: put a version in a channel. The rules, and why:
/// <list type="bullet">
/// <item>Testing, with no stable version yet: the entry is testing-exclusive, so only players with
/// testing builds see AetherFrame at all (the first pre-release is always exclusive).</item>
/// <item>Testing, with a stable version: the testing slot takes the version and the stable version
/// stays. Testers always get at least the stable version, so an older testing version is refused,
/// and naming the stable version itself clears the testing slot.</item>
/// <item>Stable: the stable slot takes the version. A testing version newer than it stays offered to
/// testers; an older or equal one is dropped, since Dalamud would ignore it.</item>
/// <item>A channel never moves to an older version unless the request says rollback, because Dalamud
/// never downgrades players who already updated. A rollback that would not move the channel back is
/// refused, so the history of the branch says what really happened. Asking for what a channel
/// already serves is harmless: nothing is written.</item>
/// </list>
/// Nothing here can empty the repository: removing the only published version is not an operation.
/// </summary>
public sealed class PublicationPlan
{
    private PublicationPlan(RepositoryState current, RepositoryState target, ProductVersion version, ReleaseChannel channel, bool rollback, ProductVersion? before, PublicationChange change)
    {
        Current = current;
        Target = target;
        Version = version;
        Channel = channel;
        Rollback = rollback;
        Before = before;
        Change = change;
    }

    public RepositoryState Current { get; }

    public RepositoryState Target { get; }

    public ProductVersion Version { get; }

    public ReleaseChannel Channel { get; }

    public bool Rollback { get; }

    /// <summary>What the requested channel served before, or null when it served nothing.</summary>
    public ProductVersion? Before { get; }

    public PublicationChange Change { get; }

    /// <summary>The releases the target describes, oldest first; each is verified before anything is generated.</summary>
    public IReadOnlyList<ProductVersion> Releases =>
        new[] { Target.Stable, Target.Testing }.Where(v => v is not null).Select(v => v!.Value).Distinct().OrderBy(v => v).ToList();

    /// <summary>A short imperative title, used as the publication commit's subject.</summary>
    public string Title => Change switch
    {
        PublicationChange.Unchanged => $"{Version} is already on {Channel.Name()}",
        PublicationChange.Rollback => $"Roll back {Channel.Name()} from {Before} to {Version}",
        _ when Channel == ReleaseChannel.Stable && Current.Testing == Version => $"Promote {Version} from testing to stable",
        _ => $"Publish {Version} to {Channel.Name()}",
    };

    public static PublicationPlan Compute(RepositoryState current, ProductVersion version, ReleaseChannel channel, bool rollback)
    {
        ProductVersion? before;
        RepositoryState target;
        if (channel == ReleaseChannel.Testing)
        {
            before = current.TestingChannel;
            if (current.Stable is { } stable)
            {
                if (version < stable)
                {
                    throw new ReleaseCheckException($"the testing channel cannot serve {version}: players with testing builds always get at least the stable version, {stable}. To move stable back, choose the stable channel with rollback.");
                }

                target = version == stable ? RepositoryState.StableOnly(stable) : RepositoryState.StableAndTesting(stable, version);
            }
            else
            {
                target = RepositoryState.TestingExclusive(version);
            }
        }
        else
        {
            before = current.StableChannel;
            target = current.Testing is { } testing && testing > version
                ? RepositoryState.StableAndTesting(version, testing)
                : RepositoryState.StableOnly(version);
        }

        PublicationChange change;
        if (before is null)
        {
            if (rollback)
            {
                throw new ReleaseCheckException($"there is nothing to roll back: the {channel.Name()} channel serves no version yet ({current.Describe()}).");
            }

            change = PublicationChange.Publish;
        }
        else if (version == before)
        {
            change = PublicationChange.Unchanged;
        }
        else if (version > before)
        {
            if (rollback)
            {
                throw new ReleaseCheckException($"{version} is newer than the {before} the {channel.Name()} channel serves now; a rollback only moves a channel to an older version. Run again without rollback.");
            }

            change = PublicationChange.Publish;
        }
        else
        {
            if (!rollback)
            {
                throw new ReleaseCheckException($"{version} is older than the {before} the {channel.Name()} channel serves now, and Dalamud never downgrades players who already have {before}. To move the channel back anyway, run again with rollback.");
            }

            change = PublicationChange.Rollback;
        }

        if (change == PublicationChange.Unchanged && target != current)
        {
            // Asking for the version a channel serves reproduces the current state by construction.
            throw new ReleaseCheckException($"internal error: {version} on {channel.Name()} leaves {current.Describe()} as {target.Describe()}.");
        }

        return new PublicationPlan(current, target, version, channel, rollback, before, change);
    }
}
