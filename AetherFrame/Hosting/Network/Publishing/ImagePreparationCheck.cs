using System;
using System.Threading.Tasks;
using AetherFrame.Services.Diagnostics;
using AetherFrame.Services.Lifecycle;
using AetherFrame.Services.Network.Publishing;

namespace AetherFrame.Hosting.Network.Publishing;

/// <summary>
/// Image preparation's known-answer check (<see cref="ImagePreparer.SelfTestAsync"/>, D5's N2-6
/// note, (3)) against Dalamud's texture pipeline, as the plugin runs it: once a session, before any
/// copy is prepared. A failed check, or any exception, answers false, and preparing copies stays off
/// until AetherFrame starts again. It runs as an owned operation, which unloading cancels, so
/// nothing of it calls Dalamud's texture services once the plugin has unloaded; a check stopped
/// that way answers false too. The log names the result and an exception's kind, never its text;
/// a failed check adds its diagnosis (<see cref="ImagePreparer.DiagnoseAsync"/>), which describes
/// only the check's own known image.
/// Compiled only in the networking preview flavour.
/// </summary>
internal static class ImagePreparationCheck
{
    internal static async Task<bool> RunAsync(IImageCodec codec, IAetherFrameLog log, OwnedOperations operations)
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(operations);
        if (!operations.TryBegin(out var lease))
        {
            return false;
        }

        using (lease)
        {
            try
            {
                var passed = await ImagePreparer.SelfTestAsync(codec, operations.Stopping).ConfigureAwait(false);
                if (passed)
                {
                    log.Information("Sharing: image preparation's check passed.");
                    return true;
                }

                log.Warning("Sharing: image preparation's check failed, so sharing is off for this session. " + await DiagnoseAsync(codec, operations).ConfigureAwait(false));
                return false;
            }
            catch (OperationCanceledException) when (operations.Stopping.IsCancellationRequested)
            {
                // Unloading: nothing more is prepared in this session anyway.
                return false;
            }
            catch (Exception e)
            {
                log.Warning("Sharing: image preparation's check failed, so sharing is off for this session: " + PublishOutcome.Describe(e) + ". " + await DiagnoseAsync(codec, operations).ConfigureAwait(false));
                return false;
            }
        }
    }

    /// <summary>The diagnosis, or why there is none: it must never turn a failed check into a failed load.</summary>
    private static async Task<string> DiagnoseAsync(IImageCodec codec, OwnedOperations operations)
    {
        try
        {
            return "What happened: " + await ImagePreparer.DiagnoseAsync(codec, operations.Stopping).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            return "Its diagnosis failed too: " + PublishOutcome.Describe(e);
        }
    }
}
