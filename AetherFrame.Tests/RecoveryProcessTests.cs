using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services.Plates;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Crash recovery against a real process termination: a disposable process (<see cref="RecoveryCrashHost"/>)
/// edits a Plate of a temporary Library with continuous recovery running on real files, and is killed
/// at different moments, mid-write included. The next start must offer the newest intact checkpoint,
/// resume it unsaved, and never have touched the saved Plate. No game and no real Library are involved.
/// </summary>
public class RecoveryProcessTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(170)]
    [InlineData(430)]
    public async Task AKilledProcess_LeavesItsNewestIntactCheckpoint_OnOffer(int killAfterMs)
    {
        using var fixture = new LibraryFixture();
        var setup = await GameSession.StartAsync(fixture);
        var plateId = await setup.CreatePlateAsync(name: "Disposable crash test");
        var platePath = fixture.Paths.GetPlatePath(plateId);
        var savedBytes = File.ReadAllBytes(platePath);
        var ready = Path.Combine(fixture.Root, "crash-host.ready");

        var errors = new StringBuilder();
        using (var host = StartHost(fixture.Root, plateId, ready, errors))
        {
            try
            {
                var waited = Stopwatch.StartNew();
                while (!File.Exists(ready))
                {
                    Assert.False(host.HasExited, $"The crash host stopped on its own (exit {(host.HasExited ? host.ExitCode : 0)}): {errors}");
                    Assert.True(waited.Elapsed < TimeSpan.FromSeconds(60), "The crash host never wrote its checkpoints.");
                    await Task.Delay(20);
                }

                await Task.Delay(killAfterMs);
                Assert.False(host.HasExited, $"The crash host stopped before it was killed: {errors}");
            }
            finally
            {
                if (!host.HasExited)
                {
                    host.Kill(entireProcessTree: true);
                }

                Assert.True(host.WaitForExit(30_000), "The crash host didn't end once killed.");
            }
        }

        // The next start: one offer for the editing, its newest intact point, every earlier one listed.
        var next = await GameSession.StartAsync(fixture);
        var offered = Assert.Single(await next.LoadKeptChangesAsync());
        Assert.True(offered.IsCheckpoint);
        Assert.Equal(plateId, offered.PlateId);
        Assert.Equal(KeptChangesChoice.Restore, offered.Choice);
        Assert.InRange(offered.Older.Count, RecoveryCrashHost.ReadyAfter - 1, RecoveryCheckpointStore.KeptPerEdit - 1);

        // Each point holds the edits made before it, in full: "edit 1" up to its own last one.
        var newest = EditsIn(offered.Draft.Document);
        Assert.True(newest.Length >= RecoveryCrashHost.ReadyAfter);
        Assert.Equal(Enumerable.Range(1, newest.Length), newest);
        Assert.All(offered.Older, o =>
        {
            var older = EditsIn(o.Draft.Document);
            Assert.Equal(Enumerable.Range(1, older.Length), older);
            Assert.True(older.Length < newest.Length);
        });
        Assert.Equal(savedBytes, File.ReadAllBytes(platePath));

        // Resume Editing: the changes are back in the editor, unsaved, and the Plate file is untouched.
        next.Offer.Choose();
        Assert.Null(next.Offer.Error);
        Assert.Equal(plateId, next.Profiles.OpenPlateId);
        Assert.True(next.Session.IsDirty);
        Assert.Equal(newest, EditsIn(next.Document));
        Assert.Equal(savedBytes, File.ReadAllBytes(platePath));
        Assert.All(KeptFiles.Checkpoints(fixture.Paths), p => Assert.Contains(next.Recovery.Store.SessionId.ToString("N"), p, StringComparison.Ordinal));
    }

    private static int[] EditsIn(ProfileDocument document) =>
        document.Elements.OfType<TextProfileElement>()
            .Select(t => int.Parse(t.Text["edit ".Length..], System.Globalization.CultureInfo.InvariantCulture))
            .Order()
            .ToArray();

    private static Process StartHost(string root, Guid plateId, string ready, StringBuilder errors)
    {
        var start = new ProcessStartInfo(DotnetHost())
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { typeof(RecoveryCrashHost).Assembly.Location, RecoveryCrashHost.Argument, root, plateId.ToString(), ready })
        {
            start.ArgumentList.Add(argument);
        }

        var host = Process.Start(start) ?? throw new InvalidOperationException("The crash host didn't start.");
        host.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { } line)
            {
                lock (errors)
                {
                    errors.AppendLine(line);
                }
            }
        };
        host.OutputDataReceived += (_, _) => { };
        host.BeginErrorReadLine();
        host.BeginOutputReadLine();
        return host;
    }

    // The dotnet executable running the tests (the SDK names it for child processes), or the one on PATH.
    private static string DotnetHost()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } fromSdk && File.Exists(fromSdk))
        {
            return fromSdk;
        }

        var current = Environment.ProcessPath;
        return current is not null && Path.GetFileNameWithoutExtension(current).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? current : "dotnet";
    }
}
