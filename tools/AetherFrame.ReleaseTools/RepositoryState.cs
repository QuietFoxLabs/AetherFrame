namespace AetherFrame.ReleaseTools;

/// <summary>The two audiences a publication addresses (docs/CustomRepository.md, Channels).</summary>
public enum ReleaseChannel
{
    /// <summary>Players with Dalamud's "Get plugin testing builds" on who opted into AetherFrame's testing.</summary>
    Testing,

    /// <summary>Every player.</summary>
    Stable,
}

public static class ReleaseChannels
{
    public static ReleaseChannel Parse(string? text) => text switch
    {
        "testing" => ReleaseChannel.Testing,
        "stable" => ReleaseChannel.Stable,
        _ => throw new ReleaseCheckException($"channel '{text}' is neither 'testing' nor 'stable'."),
    };

    public static string Name(this ReleaseChannel channel) => channel == ReleaseChannel.Testing ? "testing" : "stable";
}

/// <summary>
/// The versions a pluginmaster.json offers, in Dalamud's model of one entry with a stable slot and a
/// testing slot: nothing yet; a stable version; a stable version and a newer testing version; or a
/// testing-exclusive version that only players with testing builds see. Only versions: everything
/// else in an entry is regenerated from the verified releases on every publication.
/// </summary>
public sealed record RepositoryState
{
    public static readonly RepositoryState Empty = new(null, null);

    private RepositoryState(ProductVersion? stable, ProductVersion? testing)
    {
        Stable = stable;
        Testing = testing;
    }

    /// <summary>The version everyone is offered, or null when there is none (empty, or testing-exclusive).</summary>
    public ProductVersion? Stable { get; }

    /// <summary>The testing slot: a version newer than <see cref="Stable"/>, or the only version of a testing-exclusive entry.</summary>
    public ProductVersion? Testing { get; }

    public bool IsEmpty => Stable is null && Testing is null;

    public bool IsTestingExclusive => Stable is null && Testing is not null;

    /// <summary>What the testing channel serves: the testing version, or else the stable one, which testers get too.</summary>
    public ProductVersion? TestingChannel => Testing ?? Stable;

    /// <summary>What the stable channel serves.</summary>
    public ProductVersion? StableChannel => Stable;

    public static RepositoryState StableOnly(ProductVersion stable) => new(stable, null);

    public static RepositoryState StableAndTesting(ProductVersion stable, ProductVersion testing)
    {
        if (testing <= stable)
        {
            throw new ReleaseCheckException($"the testing version {testing} must be newer than the stable version {stable}; Dalamud ignores it otherwise.");
        }

        return new RepositoryState(stable, testing);
    }

    public static RepositoryState TestingExclusive(ProductVersion testing) => new(null, testing);

    /// <summary>The state an entry describes. The entry must already have passed <see cref="RepositoryValidator"/>.</summary>
    public static RepositoryState FromEntry(RepositoryEntry entry)
    {
        var stable = ProductVersion.FromAssemblyVersion(entry.AssemblyVersion, "AssemblyVersion");
        if (entry.IsTestingExclusive == true)
        {
            return TestingExclusive(stable);
        }

        return entry.TestingAssemblyVersion is null
            ? StableOnly(stable)
            : StableAndTesting(stable, ProductVersion.FromAssemblyVersion(entry.TestingAssemblyVersion, "TestingAssemblyVersion"));
    }

    /// <summary>"stable 0.1.5, testing 0.1.6", "testing-exclusive 0.1.6" or "nothing published".</summary>
    public string Describe()
    {
        if (IsEmpty)
        {
            return "nothing published";
        }

        if (IsTestingExclusive)
        {
            return $"testing-exclusive {Testing}";
        }

        return Testing is null ? $"stable {Stable}" : $"stable {Stable}, testing {Testing}";
    }

    public override string ToString() => Describe();
}
