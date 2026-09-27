using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace AetherFrame.ReleaseTools.Tests;

/// <summary>
/// The committed publication workflow keeps the properties its safety rests on (docs/CustomRepository.md,
/// Publishing): started only by hand, read-only except for the one environment-gated job that pushes,
/// one run at a time, actions pinned by commit, request inputs never spliced into a script, and writing
/// only the branch that players' repository URL reads. The file is read as lines; it is kept in the
/// plain style these checks expect.
/// </summary>
public class PublicationWorkflowTests
{
    private const string WorkflowPath = ".github/workflows/publish-custom-repository.yml";
    private const string ScriptPath = ".github/scripts/custom-repository.sh";

    [Fact]
    public void StartsOnlyByHand()
    {
        var lines = Lines(WorkflowPath);

        Assert.Equal(new[] { "workflow_dispatch" }, Keys(Children(lines, "on:"), 2));
        Assert.Equal(new[] { "version", "channel", "rollback", "publish" }, Keys(Children(lines, "    inputs:"), 6));
        // Unticked, a run is a dry run.
        Assert.Contains("        default: false", Children(lines, "      publish:"));
    }

    [Fact]
    public void IsReadOnly_ExceptTheEnvironmentGatedPublishJob()
    {
        var lines = Lines(WorkflowPath);

        Assert.Equal(new[] { "  contents: read" }, Children(lines, "permissions:"));
        Assert.Equal(new[] { "prepare", "publish" }, Keys(Children(lines, "jobs:"), 2));
        Assert.DoesNotContain(Children(lines, "  prepare:"), l => l.Trim() == "permissions:");

        var publish = Children(lines, "  publish:");
        Assert.Contains("    needs: prepare", publish);
        Assert.Contains("    environment: custom-repository", publish);
        Assert.Equal(new[] { "      contents: write" }, Children(lines, "    permissions:"));
        Assert.Single(lines, l => l.Trim().StartsWith("contents: write", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => Regex.IsMatch(l, @"^\s*(actions|checks|deployments|id-token|issues|packages|pages|pull-requests|statuses|security-events):"));
    }

    [Fact]
    public void RunsOnePublicationAtATime()
    {
        var concurrency = Children(Lines(WorkflowPath), "concurrency:");

        Assert.Equal(new[] { "  group: publish-custom-repository", "  cancel-in-progress: false" }, concurrency);
    }

    [Fact]
    public void PinsEveryActionToACommit()
    {
        var uses = Lines(WorkflowPath).Where(l => l.TrimStart().StartsWith("uses:", StringComparison.Ordinal)).ToList();

        Assert.NotEmpty(uses);
        Assert.All(uses, l => Assert.Matches(@"^\s+uses: actions/[a-z-]+@[0-9a-f]{40} # v\d+\.\d+\.\d+$", l));
    }

    [Fact]
    public void NeverSplicesAnExpressionIntoAScript()
    {
        // Request inputs and other expressions reach the shell only as environment variables, so no input
        // text can become part of a command.
        var lines = Lines(WorkflowPath);
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith("run:", StringComparison.Ordinal))
            {
                continue;
            }

            var script = new List<string> { trimmed };
            if (trimmed == "run: |")
            {
                var indent = Indent(lines[i]);
                script.AddRange(lines.Skip(i + 1).TakeWhile(l => l.Trim().Length == 0 || Indent(l) > indent));
            }

            Assert.DoesNotContain(script, l => l.Contains("${{", StringComparison.Ordinal));
        }

        Assert.All(
            lines.Where(l => l.Contains("inputs.", StringComparison.Ordinal) && l.Contains("${{", StringComparison.Ordinal)),
            l => Assert.Matches(@"^\s+[A-Z_]+: \$\{\{ inputs\.[a-z]+ \}\}$", l));
        Assert.DoesNotContain(lines, l => l.Contains("github.event.inputs", StringComparison.Ordinal));
    }

    [Fact]
    public void WritesTheBranchTheRepositoryUrlServes()
    {
        var configuration = RepositoryConfiguration.Load(RepositoryPaths.File(Path.Combine("distribution", "repository.json")));
        var target = PublicationTarget.FromConfiguration(configuration);

        Assert.Contains($"  PUBLICATION_BRANCH: {target.Branch}", Lines(WorkflowPath));
    }

    [Fact]
    public void TheScriptNeverForcePushes()
    {
        var pushes = Lines(ScriptPath).Where(l => Regex.IsMatch(l, @"\bgit\b.*\bpush\b")).ToList();

        var push = Assert.Single(pushes);
        Assert.DoesNotContain("--force", push);
        Assert.DoesNotMatch(@"\s-f\b", push);
        Assert.Contains("\"$commit:refs/heads/$PUBLICATION_BRANCH\"", push);
    }

    [Fact]
    public void TheBranchReadme_IsWhatThePublicationAccepts()
    {
        var readme = File.ReadAllBytes(RepositoryPaths.File(Path.Combine("distribution", "plugin-repository", "README.md")));

        Assert.Same(readme, Publication.CheckReadme(readme));
    }

    private static IReadOnlyList<string> Lines(string relativePath) => File.ReadAllLines(RepositoryPaths.File(relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static int Indent(string line) => line.Length - line.TrimStart().Length;

    /// <summary>The lines nested under the line equal to <paramref name="header"/>, without comments and blank lines.</summary>
    private static List<string> Children(IReadOnlyList<string> lines, string header)
    {
        var start = lines.ToList().IndexOf(header);
        Assert.True(start >= 0, $"{WorkflowPath} has no line '{header}'.");
        var indent = Indent(header);
        return lines.Skip(start + 1)
            .Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#'))
            .TakeWhile(l => Indent(l) > indent)
            .ToList();
    }

    /// <summary>The mapping keys at exactly <paramref name="indent"/> spaces.</summary>
    private static List<string> Keys(IEnumerable<string> lines, int indent) =>
        lines.Where(l => Indent(l) == indent && !l.TrimStart().StartsWith('-')).Select(l => l.Trim().Split(':')[0]).ToList();
}
