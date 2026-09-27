using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AetherFrame.ReleaseTools;

/// <summary>Everything one publication is prepared from. Only the configuration, the request and the fetched files.</summary>
public sealed class PublicationRequest
{
    public required RepositoryConfiguration Configuration { get; init; }

    /// <summary>The branch the workflow writes, which must be the one pluginMasterUrl serves.</summary>
    public required string Branch { get; init; }

    /// <summary>The published pluginmaster.json, or null when nothing has been published yet.</summary>
    public byte[]? Current { get; init; }

    /// <summary>The publication branch's commit the current file was read from, for the record.</summary>
    public string? CurrentCommit { get; init; }

    public required ProductVersion Version { get; init; }

    public required ReleaseChannel Channel { get; init; }

    public bool Rollback { get; init; }

    /// <summary>The directory holding one v&lt;version&gt; directory per release the plan needs.</summary>
    public required string ReleasesDirectory { get; init; }

    /// <summary>The README.md the publication branch carries next to pluginmaster.json.</summary>
    public required byte[] BranchReadme { get; init; }

    /// <summary>The workflow run doing the publication, for the record.</summary>
    public string? RunUrl { get; init; }
}

/// <summary>A publication that passed every check: the two files for the branch and the record of what they are.</summary>
public sealed record PreparedPublication(PublicationTarget Target, PublicationPlan Plan, byte[] Document, byte[] Readme, bool Changed, PublicationSummary Summary, string Report, string CommitMessage);

/// <summary>
/// Prepares a publication of the custom repository (docs/CustomRepository.md, Publishing): reads the
/// published file's state, plans the change the request asks for, verifies every release the new
/// file will describe, generates the file, and checks it against those releases before it exists.
/// The result is written by the workflow only after all of that passed, as one commit.
/// </summary>
public static class Publication
{
    public const string BranchDirectory = "branch";
    public const string ReadmeFileName = "README.md";
    public const string SummaryFileName = "summary.json";
    public const string ReportFileName = "report.md";
    public const string CommitMessageFileName = "commit-message.txt";
    public const int MaxReadmeBytes = 16 * 1024;

    private static readonly Regex Sha1 = new(@"^[0-9a-f]{40}$", RegexOptions.CultureInvariant);
    private static readonly Regex RunPath = new(@"^/actions/runs/[0-9]{1,20}(/attempts/[0-9]{1,5})?$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The state the published file describes. The file must pass every structural check first: a
    /// damaged, foreign or hand-edited file stops the publication instead of being built upon. It is
    /// not compared with CHANGELOG.md, which may have moved on since it was published.
    /// </summary>
    public static RepositoryState ReadCurrent(byte[]? current, RepositoryConfiguration config, CheckList checks)
    {
        if (current is null)
        {
            checks.Pass("published file", "none: this is the first publication");
            return RepositoryState.Empty;
        }

        var own = new CheckList();
        IReadOnlyList<RepositoryEntry>? entries = null;
        try
        {
            entries = RepositoryValidator.Validate(new RepositoryValidationRequest { Document = current, What = "published pluginmaster.json", Configuration = config }, own);
        }
        catch (ReleaseCheckException e)
        {
            if (!own.HasFailures)
            {
                own.Fail("document", e.Message);
            }
        }
        finally
        {
            Include(checks, "published file", own);
        }

        if (entries is null)
        {
            throw new ReleaseCheckException("the published pluginmaster.json does not pass validation; nothing is built on it.");
        }

        var state = RepositoryState.FromEntry(entries[0]);
        checks.Pass("published state", $"{state.Describe()}, SHA-256 {Checksums.Sha256Hex(current)}");
        return state;
    }

    public static PreparedPublication Prepare(PublicationRequest request, CheckList checks)
    {
        var config = request.Configuration;
        var target = checks.Attempt("publication target", () => PublicationTarget.Resolve(config, request.Branch), t => $"branch {t.Branch}, served at {t.Url}");
        var readme = checks.Attempt("branch README", () => CheckReadme(request.BranchReadme), r => $"{r.Length} bytes");
        if (request.CurrentCommit is not null)
        {
            checks.Require(Sha1.IsMatch(request.CurrentCommit), "published commit", request.CurrentCommit, $"'{request.CurrentCommit}' is not a 40-character lowercase commit id.");
        }

        if (request.RunUrl is not null)
        {
            checks.Attempt("workflow run", () => CheckRunUrl(request.RunUrl, config), u => u);
        }

        checks.ThrowIfFailed();

        var current = ReadCurrent(request.Current, config, checks);
        var plan = checks.Attempt("plan", () => PublicationPlan.Compute(current, request.Version, request.Channel, request.Rollback), p => $"{p.Title}: {p.Current.Describe()} -> {p.Target.Describe()}");
        if (plan is null)
        {
            throw new ReleaseCheckException("the request cannot be applied to the published repository.");
        }

        // Every release the new file describes is verified from scratch, including one that is already
        // published and stays: nothing about it is carried over from the published file but its version.
        var verified = new Dictionary<ProductVersion, VerifiedRelease>();
        foreach (var version in plan.Releases)
        {
            try
            {
                verified[version] = ReleaseVerifier.Verify(ReleaseVerifier.DirectoryFor(request.ReleasesDirectory, version), version, config, stableSlot: plan.Target.Stable == version, checks);
            }
            catch (ReleaseCheckException e)
            {
                if (!checks.HasFailures)
                {
                    checks.Fail($"{version.Tag}: release", e.Message);
                }
            }
        }

        checks.ThrowIfFailed();

        var exclusive = plan.Target.IsTestingExclusive;
        var stable = plan.Target.Stable is { } stableVersion ? verified[stableVersion] : null;
        var testing = plan.Target.Testing is { } testingVersion ? verified[testingVersion] : null;

        // The newest release instant among the releases described: the installer's "Last Update".
        var lastUpdate = verified.Values.Max(r => r.PublishedAt);
        var entry = checks.Attempt("repository entry", () => RepositoryGenerator.Build(config, stable?.Package, testing?.Package, exclusive, lastUpdate), e => plan.Target.Describe());
        if (entry is null)
        {
            throw new ReleaseCheckException("the repository entry could not be built.");
        }

        var document = RepositoryDocument.Serialize(new[] { entry });

        // The new file must pass its own validation, against exactly the packages it describes.
        var own = new CheckList();
        try
        {
            RepositoryValidator.Validate(new RepositoryValidationRequest
            {
                Document = document,
                What = "new pluginmaster.json",
                Configuration = config,
                StablePackage = exclusive ? testing!.Package : stable!.Package,
                TestingPackage = exclusive ? null : testing?.Package,
            }, own);
        }
        catch (ReleaseCheckException e)
        {
            if (!own.HasFailures)
            {
                own.Fail("document", e.Message);
            }
        }
        finally
        {
            Include(checks, "new file", own);
        }

        checks.ThrowIfFailed();
        var produced = RepositoryState.FromEntry(RepositoryDocument.Parse(document, "new pluginmaster.json")[0]);
        checks.Require(produced == plan.Target, "new state", produced.Describe(), $"the new file describes {produced.Describe()}, but the plan is {plan.Target.Describe()}.");
        checks.ThrowIfFailed();

        var changed = request.Current is null || !request.Current.AsSpan().SequenceEqual(document);
        var summary = Summarize(request, target!, plan, verified.Values.OrderBy(r => r.Version).ToList(), document, changed);
        return new PreparedPublication(target!, plan, document, readme!, changed, summary, Report(summary), CommitMessage(summary));
    }

    /// <summary>The README the branch carries: short UTF-8 text without a byte order mark.</summary>
    public static byte[] CheckReadme(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaxReadmeBytes)
        {
            throw new ReleaseCheckException($"is {bytes.Length} bytes; it must be between 1 and {MaxReadmeBytes}.");
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            throw new ReleaseCheckException("starts with a UTF-8 byte order mark.");
        }

        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new ReleaseCheckException("is not valid UTF-8.");
        }

        if (text.Any(c => c == '\0'))
        {
            throw new ReleaseCheckException("contains a NUL character.");
        }

        return bytes;
    }

    private static string CheckRunUrl(string url, RepositoryConfiguration config)
    {
        var uri = Urls.ValidateHttps(url, "the workflow run URL");
        var prefix = config.SourceRepositoryUrl;
        if (!url.StartsWith(prefix + "/", StringComparison.Ordinal) || !RunPath.IsMatch(url[prefix.Length..]))
        {
            throw new ReleaseCheckException($"'{uri}' is not a workflow run of {prefix}.");
        }

        return url;
    }

    private static void Include(CheckList checks, string prefix, CheckList own)
    {
        foreach (var check in own.Checks)
        {
            if (check.Passed)
            {
                checks.Pass($"{prefix}: {check.Name}", check.Detail);
            }
            else
            {
                checks.Fail($"{prefix}: {check.Name}", check.Detail);
            }
        }
    }

    private static PublicationSummary Summarize(PublicationRequest request, PublicationTarget target, PublicationPlan plan, IReadOnlyList<VerifiedRelease> releases, byte[] document, bool changed)
    {
        string Slot(ProductVersion version) =>
            plan.Target.IsTestingExclusive ? "testing-exclusive" : plan.Target.Stable == version ? "stable" : "testing";

        var title = plan.Change == PublicationChange.Unchanged && changed
            ? $"Regenerate {plan.Target.Describe()}"
            : plan.Title;

        return new PublicationSummary
        {
            InternalName = request.Configuration.InternalName,
            PluginMasterUrl = target.Url,
            Branch = target.Branch,
            Request = new PublicationRequestSummary { Version = request.Version.ToString(), Channel = request.Channel.Name(), Rollback = request.Rollback },
            Change = plan.Change.ToString().ToLowerInvariant(),
            Title = title,
            Changed = changed,
            Previous = new PublishedFileSummary
            {
                State = plan.Current.Describe(),
                Commit = request.CurrentCommit,
                Sha256 = request.Current is null ? null : Checksums.Sha256Hex(request.Current),
                Size = request.Current?.Length,
            },
            Next = new PublishedFileSummary { State = plan.Target.Describe(), Sha256 = Checksums.Sha256Hex(document), Size = document.Length },
            ChangedFields = request.Current is null ? null : ChangedFields(request.Current, document),
            Releases = releases.Select(r => new ReleaseSummary
            {
                Slot = Slot(r.Version),
                Version = r.Version.ToString(),
                Tag = r.Version.Tag,
                Commit = r.Commit,
                ReleaseId = r.Release.Id,
                ReleaseUrl = r.Release.HtmlUrl,
                PublishedAt = r.PublishedAt.ToString("O", CultureInfo.InvariantCulture),
                Prerelease = r.Release.Prerelease,
                Package = new Program.FileSummary { Name = r.Package.Package.FileName, Size = r.Package.Package.Size, Sha256 = r.Package.Package.Sha256 },
                ChecksumsSha256 = r.ChecksumsSha256,
            }).ToList(),
            RunUrl = request.RunUrl,
        };
    }

    /// <summary>The top-level fields of the one entry whose JSON differs between two valid documents, in the new document's order.</summary>
    private static List<string> ChangedFields(byte[] before, byte[] after)
    {
        using var old = JsonDocument.Parse(before);
        using var now = JsonDocument.Parse(after);
        var oldEntry = old.RootElement[0];
        var newEntry = now.RootElement[0];
        var names = newEntry.EnumerateObject().Select(p => p.Name)
            .Concat(oldEntry.EnumerateObject().Select(p => p.Name))
            .Distinct(StringComparer.Ordinal);
        return names.Where(name =>
        {
            var inOld = oldEntry.TryGetProperty(name, out var oldValue);
            var inNew = newEntry.TryGetProperty(name, out var newValue);
            return inOld != inNew || (inOld && oldValue.GetRawText() != newValue.GetRawText());
        }).ToList();
    }

    /// <summary>The run summary: what changes, and every release checked, for the reviewer who approves the publication.</summary>
    public static string Report(PublicationSummary summary)
    {
        var builder = new StringBuilder();
        builder.Append("## Custom repository: ").Append(summary.Title).Append("\n\n");
        builder.Append("| | Before | After |\n|---|---|---|\n");
        builder.Append("| Offers | ").Append(summary.Previous!.State).Append(" | ").Append(summary.Next!.State).Append(" |\n");
        builder.Append("| `pluginmaster.json` SHA-256 | ").Append(Code(summary.Previous.Sha256)).Append(" | ").Append(Code(summary.Next.Sha256)).Append(" |\n");
        builder.Append("| Size in bytes | ").Append(summary.Previous.Size?.ToString(CultureInfo.InvariantCulture) ?? "none").Append(" | ").Append(summary.Next.Size?.ToString(CultureInfo.InvariantCulture)).Append(" |\n");
        builder.Append("| `").Append(summary.Branch).Append("` commit | ").Append(Code(summary.Previous.Commit)).Append(" | a new commit, when published |\n\n");

        builder.Append("Requested: ").Append(summary.Request!.Version).Append(" to ").Append(summary.Request.Channel).Append(summary.Request.Rollback ? ", as a rollback" : string.Empty).Append(". ");
        if (!summary.Changed)
        {
            builder.Append("**Nothing to publish:** the branch already serves exactly this file.\n\n");
        }
        else if (summary.ChangedFields is null)
        {
            builder.Append("**First publication.**\n\n");
        }
        else
        {
            builder.Append("Fields that change: ").Append(string.Join(", ", summary.ChangedFields.Select(f => "`" + f + "`"))).Append(".\n\n");
        }

        builder.Append("| Slot | Release | Published | Kind | Tagged commit | Package (GitHub digest, `SHA256SUMS.txt` and download agree) |\n|---|---|---|---|---|---|\n");
        foreach (var release in summary.Releases!)
        {
            builder.Append("| ").Append(release.Slot)
                .Append(" | [").Append(release.Tag).Append("](").Append(release.ReleaseUrl).Append(") (id ").Append(release.ReleaseId.ToString(CultureInfo.InvariantCulture)).Append(')')
                .Append(" | ").Append(release.PublishedAt)
                .Append(" | ").Append(release.Prerelease ? "pre-release" : "full release")
                .Append(" | `").Append(release.Commit).Append('`')
                .Append(" | `").Append(release.Package!.Name).Append("`, ").Append(release.Package.Size.ToString(CultureInfo.InvariantCulture)).Append(" bytes, `sha256:").Append(release.Package.Sha256).Append("` |\n");
        }

        builder.Append("\nBranch `").Append(summary.Branch).Append("`, served at ").Append(summary.PluginMasterUrl).Append(".\n");
        return builder.ToString();

        static string Code(string? value) => value is null ? "none" : "`" + value + "`";
    }

    /// <summary>The publication commit's message: the permanent record, in the branch's own history, of what was published and from what.</summary>
    public static string CommitMessage(PublicationSummary summary)
    {
        var builder = new StringBuilder();
        builder.Append(summary.Title).Append("\n\n");
        builder.Append(summary.InternalName).Append(" custom repository: ").Append(summary.Next!.State).Append(" (was: ").Append(summary.Previous!.State).Append(").\n");
        builder.Append("Requested: ").Append(summary.Request!.Version).Append(" to ").Append(summary.Request.Channel).Append(summary.Request.Rollback ? ", rollback" : string.Empty).Append(".\n");
        builder.Append("pluginmaster.json: sha256 ").Append(summary.Next.Sha256).Append(", ").Append(summary.Next.Size?.ToString(CultureInfo.InvariantCulture)).Append(" bytes.\n");
        builder.Append("Previous pluginmaster.json: ").Append(summary.Previous.Sha256 is null ? "none" : "sha256 " + summary.Previous.Sha256).Append(".\n");
        if (summary.Previous.Commit is not null)
        {
            builder.Append("Previous commit: ").Append(summary.Previous.Commit).Append(".\n");
        }

        foreach (var release in summary.Releases!)
        {
            builder.Append('\n').Append("Release ").Append(release.Tag).Append(" (").Append(release.Slot).Append("): ").Append(release.ReleaseUrl).Append('\n');
            builder.Append("  release id ").Append(release.ReleaseId.ToString(CultureInfo.InvariantCulture)).Append(", published ").Append(release.PublishedAt).Append(", ").Append(release.Prerelease ? "pre-release" : "full release").Append('\n');
            builder.Append("  tagged commit ").Append(release.Commit).Append('\n');
            builder.Append("  ").Append(release.Package!.Name).Append(": ").Append(release.Package.Size.ToString(CultureInfo.InvariantCulture)).Append(" bytes, sha256 ").Append(release.Package.Sha256).Append('\n');
        }

        if (summary.RunUrl is not null)
        {
            builder.Append("\nWorkflow run: ").Append(summary.RunUrl).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>The branch contents, keyed by file name: exactly what the publication commit's tree holds.</summary>
    public static IReadOnlyList<(string Name, byte[] Content)> BranchFiles(PreparedPublication prepared) =>
        new[] { (ReadmeFileName, prepared.Readme), (PublicationTarget.FileName, prepared.Document) };
}

/// <summary>The machine-readable publication record (summary.json).</summary>
public sealed class PublicationSummary
{
    [JsonPropertyName("internalName"), JsonPropertyOrder(0)]
    public string? InternalName { get; set; }

    [JsonPropertyName("pluginMasterUrl"), JsonPropertyOrder(1)]
    public string? PluginMasterUrl { get; set; }

    [JsonPropertyName("branch"), JsonPropertyOrder(2)]
    public string? Branch { get; set; }

    [JsonPropertyName("request"), JsonPropertyOrder(3)]
    public PublicationRequestSummary? Request { get; set; }

    /// <summary>"publish", "rollback" or "unchanged": what the plan does to the requested channel.</summary>
    [JsonPropertyName("change"), JsonPropertyOrder(4)]
    public string? Change { get; set; }

    [JsonPropertyName("title"), JsonPropertyOrder(5)]
    public string? Title { get; set; }

    /// <summary>Whether the new pluginmaster.json differs from the published one, byte for byte.</summary>
    [JsonPropertyName("changed"), JsonPropertyOrder(6)]
    public bool Changed { get; set; }

    [JsonPropertyName("previous"), JsonPropertyOrder(7)]
    public PublishedFileSummary? Previous { get; set; }

    [JsonPropertyName("next"), JsonPropertyOrder(8)]
    public PublishedFileSummary? Next { get; set; }

    [JsonPropertyName("changedFields"), JsonPropertyOrder(9)]
    public List<string>? ChangedFields { get; set; }

    [JsonPropertyName("releases"), JsonPropertyOrder(10)]
    public List<ReleaseSummary>? Releases { get; set; }

    [JsonPropertyName("runUrl"), JsonPropertyOrder(11)]
    public string? RunUrl { get; set; }
}

public sealed class PublicationRequestSummary
{
    [JsonPropertyName("version"), JsonPropertyOrder(0)]
    public string? Version { get; set; }

    [JsonPropertyName("channel"), JsonPropertyOrder(1)]
    public string? Channel { get; set; }

    [JsonPropertyName("rollback"), JsonPropertyOrder(2)]
    public bool Rollback { get; set; }
}

public sealed class PublishedFileSummary
{
    [JsonPropertyName("state"), JsonPropertyOrder(0)]
    public string? State { get; set; }

    [JsonPropertyName("commit"), JsonPropertyOrder(1)]
    public string? Commit { get; set; }

    [JsonPropertyName("sha256"), JsonPropertyOrder(2)]
    public string? Sha256 { get; set; }

    [JsonPropertyName("size"), JsonPropertyOrder(3)]
    public long? Size { get; set; }
}

public sealed class ReleaseSummary
{
    /// <summary>"stable", "testing" or "testing-exclusive".</summary>
    [JsonPropertyName("slot"), JsonPropertyOrder(0)]
    public string? Slot { get; set; }

    [JsonPropertyName("version"), JsonPropertyOrder(1)]
    public string? Version { get; set; }

    [JsonPropertyName("tag"), JsonPropertyOrder(2)]
    public string? Tag { get; set; }

    [JsonPropertyName("commit"), JsonPropertyOrder(3)]
    public string? Commit { get; set; }

    [JsonPropertyName("releaseId"), JsonPropertyOrder(4)]
    public long ReleaseId { get; set; }

    [JsonPropertyName("releaseUrl"), JsonPropertyOrder(5)]
    public string? ReleaseUrl { get; set; }

    [JsonPropertyName("publishedAt"), JsonPropertyOrder(6)]
    public string? PublishedAt { get; set; }

    [JsonPropertyName("prerelease"), JsonPropertyOrder(7)]
    public bool Prerelease { get; set; }

    [JsonPropertyName("package"), JsonPropertyOrder(8)]
    public Program.FileSummary? Package { get; set; }

    [JsonPropertyName("checksumsSha256"), JsonPropertyOrder(9)]
    public string? ChecksumsSha256 { get; set; }
}
