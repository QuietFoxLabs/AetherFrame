using System;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Services.Diagnostics;
using AetherFrame.Services.Network.Publishing;

namespace AetherFrame.Hosting.Network.Publishing;

/// <summary>
/// Image preparation's known-answer check (<see cref="ImagePreparer.SelfTestAsync"/>, D5's N2-6
/// note, (3)) against Dalamud's texture pipeline, as the plugin runs it: once a session, before any
/// copy is prepared. A failed check, or any exception, answers false, and preparing copies stays off
/// until AetherFrame starts again. The log names the result and an exception's kind, never its text.
/// Compiled only in the networking preview flavour.
/// </summary>
internal static class ImagePreparationCheck
{
    internal static async Task<bool> RunAsync(IImageCodec codec, IAetherFrameLog log)
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(log);
        try
        {
            var passed = await ImagePreparer.SelfTestAsync(codec, CancellationToken.None).ConfigureAwait(false);
            log.Information(passed
                ? "Sharing: image preparation's check passed."
                : "Sharing: image preparation's check failed, so sharing is off for this session.");
            return passed;
        }
        catch (Exception e)
        {
            log.Warning("Sharing: image preparation's check failed, so sharing is off for this session: " + PublishOutcome.Describe(e));
            return false;
        }
    }
}
