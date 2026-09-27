using System;

namespace AetherFrame.Services.Diagnostics;

/// <summary>
/// The front of the plugin's <see cref="IAetherFrameLog"/>: every message and exception passes
/// <see cref="LogPrivacy"/> before it reaches Dalamud's log, so no character binding file (named by
/// the character's Content ID) is ever named there, whatever a message or an exception carries.
/// </summary>
internal sealed class RedactingAetherFrameLog : IAetherFrameLog
{
    private readonly IAetherFrameLog inner;

    internal RedactingAetherFrameLog(IAetherFrameLog inner)
    {
        this.inner = inner;
    }

    public void Information(string message) => inner.Information(LogPrivacy.Redact(message));

    public void Warning(string message) => inner.Warning(LogPrivacy.Redact(message));

    public void Error(Exception? exception, string message) => inner.Error(LogPrivacy.ForLog(exception), LogPrivacy.Redact(message));
}
