using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AetherFrame.Services.Plates;
using AetherFrame.Services.Templates;
using AetherFrame.UI.Library;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The operation runner My Plates and the editors' Plate menu share (interface task 1): it was My
/// Plates' own until then, so these pin exactly how it behaved there: results applied on the
/// window's next frame, one operation at a time, refusals in their own words, anything else logged
/// at once and reported plainly.
/// </summary>
public class PlateOperationRunnerTests
{
    [Fact]
    public async Task AResult_IsAppliedOnlyByAdvance()
    {
        var runner = new PlateOperationRunner(new TestLog());
        var release = new TaskCompletionSource();

        runner.Run("wait", () => release.Task, () => runner.Status = "done");
        runner.Advance();
        Assert.True(runner.IsBusy);

        release.SetResult();
        await runner.Finished;
        Assert.True(runner.IsBusy);
        Assert.Null(runner.Status);

        runner.Advance();
        Assert.False(runner.IsBusy);
        Assert.Equal("done", runner.Status);
    }

    [Fact]
    public async Task AResultWithAValue_ReachesItsCallback()
    {
        var runner = new PlateOperationRunner(new TestLog());
        var id = Guid.NewGuid();
        Guid? received = null;

        runner.Run("copy", () => Task.FromResult(id), value => received = value);
        await runner.Finished;
        runner.Advance();

        Assert.Equal(id, received);
    }

    [Fact]
    public async Task WhileOneRuns_AnotherIsRefused_AndNeverStarts()
    {
        var runner = new PlateOperationRunner(new TestLog());
        var release = new TaskCompletionSource();
        var secondStarted = false;

        runner.Run("first", () => release.Task);
        runner.Run("second", () =>
        {
            secondStarted = true;
            return Task.CompletedTask;
        });

        Assert.False(secondStarted);
        Assert.Equal(PlateOperationRunner.BusyMessage, runner.Error);
        Assert.Equal("Please wait for the current action to finish.", runner.Error);

        release.SetResult();
        await runner.Finished;
        runner.Advance();
        Assert.False(runner.IsBusy);
    }

    [Fact]
    public async Task ARefusal_ShowsItsOwnWords_AndIsNotLogged()
    {
        var log = new TestLog();
        var runner = new PlateOperationRunner(log);

        runner.Run("rename the Plate", () => Task.FromException(new PlateLibraryException("That name is too long.")));
        await runner.Finished;
        runner.Advance();
        Assert.Equal("That name is too long.", runner.Error);

        runner.Run("save the Template", () => Task.FromException(new TemplateLibraryException("That Template is gone.")));
        await runner.Finished;
        runner.Advance();
        Assert.Equal("That Template is gone.", runner.Error);

        Assert.Empty(log.Messages);
    }

    [Fact]
    public async Task AnyOtherFailure_IsLoggedAtOnce_AndReportedPlainly()
    {
        var log = new TestLog();
        var runner = new PlateOperationRunner(log);

        runner.Run("export the Plate", () => Task.FromException(new IOException(@"C:\Users\someone\secret.aetherframe is locked")));
        await runner.Finished;

        // Logged before the window draws again, which may be never if it was closed.
        Assert.Contains("E AetherFrame failed to export the Plate.", log.Messages);
        Assert.Null(runner.Error);

        runner.Advance();
        Assert.Equal("Couldn't export the Plate. See the Dalamud log for details.", runner.Error);
        Assert.Equal(PlateOperationRunner.FailedMessage("export the Plate"), runner.Error);
    }

    [Fact]
    public async Task AFailureThrownBeforeTheFirstAwait_IsHandledTheSameWay()
    {
        var log = new TestLog();
        var runner = new PlateOperationRunner(log);

        runner.Run("reorder", () => throw new InvalidOperationException("boom"));
        await runner.Finished;
        runner.Advance();

        Assert.Equal("Couldn't reorder. See the Dalamud log for details.", runner.Error);
        Assert.Single(log.Messages);
    }

    [Fact]
    public async Task Starting_ClearsTheLastMessages()
    {
        var runner = new PlateOperationRunner(new TestLog())
        {
            Error = "An old error.",
            Status = "An old result.",
        };

        runner.Run("wait", () => Task.CompletedTask);
        Assert.Null(runner.Error);
        Assert.Null(runner.Status);

        await runner.Finished;
        runner.Advance();
    }

    [Fact]
    public void EveryMessage_ChangesTheMessageVersion()
    {
        var runner = new PlateOperationRunner(new TestLog());
        var versions = new[] { runner.MessageVersion, Set(() => runner.Status = "a"), Set(() => runner.Status = "a"), Set(() => runner.Error = "b"), Set(() => runner.Error = null) };

        Assert.Equal(versions.Length, versions.Distinct().Count());

        int Set(Action set)
        {
            set();
            return runner.MessageVersion;
        }
    }

    [Fact]
    public async Task Finished_IsComplete_WhenNothingRuns_AndNeverThrows()
    {
        var runner = new PlateOperationRunner(new TestLog());
        Assert.True(runner.Finished.IsCompleted);

        runner.Run("fail", () => Task.FromException(new PlateLibraryException("No.")));
        await runner.Finished;
        runner.Advance();
        Assert.True(runner.Finished.IsCompleted);
    }
}
