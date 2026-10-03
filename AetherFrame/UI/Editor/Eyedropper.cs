using System;
using System.Numerics;

namespace AetherFrame.UI.Editor;

/// <summary>
/// A pixel of the desktop in physical screen pixels: the virtual screen, whose 0, 0 is the primary
/// monitor's top left, so a monitor left of or above it has negative coordinates.
/// </summary>
internal readonly record struct ScreenPixel(int X, int Y);

/// <summary>One answer from an <see cref="IScreenColorReader"/>: the color of the pixel asked for, or why it couldn't be read.</summary>
internal readonly record struct ScreenReading(int Request, Vector3? Color, string? Problem);

/// <summary>
/// Reads the color shown at a pixel of the desktop, for the <see cref="Eyedropper"/>. Readings may
/// come back later than asked for, on any thread; <see cref="TryTake"/> hands them over in order.
/// Nothing is read outside <see cref="Begin"/> and <see cref="End"/>.
/// </summary>
internal interface IScreenColorReader
{
    /// <summary>A pick starts: get ready to read (the game's picture, for one).</summary>
    void Begin();

    /// <summary>Asks for the color at <paramref name="at"/>; the reading comes back under the returned number.</summary>
    int Request(ScreenPixel at);

    /// <summary>The next reading, if one has come back.</summary>
    bool TryTake(out ScreenReading reading);

    /// <summary>The pick is over: nothing more is read, and readings still on their way are dropped.</summary>
    void End();
}

/// <summary>What the eyedropper sees of the player this frame (see <see cref="Eyedropper.Update"/>).</summary>
/// <param name="Frame">The frame's number.</param>
/// <param name="Milliseconds">A clock, in milliseconds.</param>
/// <param name="Pointer">Where the pointer is on the desktop, or null when that can't be told.</param>
/// <param name="PickButtonDown">The primary mouse button is down, wherever the pointer is.</param>
/// <param name="CancelButtonDown">The secondary mouse button is down.</param>
/// <param name="PickKeyPressed">Enter or Space went down this frame.</param>
/// <param name="CancelKeyPressed">Escape went down this frame.</param>
/// <param name="KeysDown">Escape, Enter or Space is down.</param>
internal readonly record struct EyedropperInput(
    int Frame,
    long Milliseconds,
    ScreenPixel? Pointer,
    bool PickButtonDown,
    bool CancelButtonDown,
    bool PickKeyPressed,
    bool CancelKeyPressed,
    bool KeysDown);

/// <summary>Where a pick is (see <see cref="Eyedropper"/>).</summary>
internal enum EyedropperPhase
{
    /// <summary>No pick: nothing is read.</summary>
    Idle,

    /// <summary>The player is pointing: the color under the pointer is shown as it moves.</summary>
    Sampling,

    /// <summary>The player picked: the color at that spot is being read, with the preview out of the way.</summary>
    Picking,

    /// <summary>The pick is over (picked or canceled): the press that ended it is kept from everything else until it is let go.</summary>
    Ending,
}

/// <summary>
/// The screen eyedropper (issue #120): a color control's eyedropper button starts a pick, the player
/// points anywhere on the desktop, and clicking (or Enter, or Space) gives that control the color
/// shown there; Escape, or the secondary button, ends it with nothing changed. The color under the
/// pointer is shown as it moves, read at most every <see cref="LivePeriodMs"/>; the picked color is
/// read again at the very spot picked, once the preview has been out of the way for
/// <see cref="PickDelayFrames"/> frames, so the preview is never picked. A spot that can't be read
/// says why and changes nothing; the player can point elsewhere or cancel.
///
/// <para>The click that started the pick is never taken as the pick: a pick by button needs the button
/// up first. The press that ends a pick is the eyedropper's until it is let go
/// (<see cref="EyedropperPhase.Ending"/>), so its release reaches nothing under the pointer.</para>
///
/// <para>Free of Dalamud and Win32: the editors feed it a frame's input and read what it shows; the
/// control that started a pick takes the picked color with <see cref="TryTakePick"/> the next time it
/// is drawn. Render thread only.</para>
/// </summary>
internal sealed class Eyedropper
{
    /// <summary>How often the color under the pointer is read while pointing.</summary>
    internal const long LivePeriodMs = 66;

    /// <summary>Frames the preview is hidden before the picked spot is read.</summary>
    internal const int PickDelayFrames = 2;

    /// <summary>How long a reading may take before it is given up on.</summary>
    internal const long ReadingTimeoutMs = 2000;

    /// <summary>Frames a picked color waits for its control to take it (one that is no longer drawn never will).</summary>
    internal const int UnclaimedFrames = 30;

    internal const string NoPointer = "Windows didn't say where the pointer is. Move it a little and try again.";
    internal const string NoAnswer = "That spot took too long to read. Try again, or press Escape.";
    internal const string Unreadable = "That spot can't be read.";

    private readonly IScreenColorReader reader;

    private bool armed;
    private bool pickWasDown;
    private bool cancelWasDown;
    private ScreenPixel pickAt;
    private int pickFrame;
    private int? pending;
    private bool pendingIsPick;
    private long pendingSinceMs;
    private long lastLiveMs;
    private (uint Owner, Vector3 Color, int Frame)? picked;

    internal Eyedropper(IScreenColorReader reader) => this.reader = reader ?? throw new ArgumentNullException(nameof(reader));

    internal EyedropperPhase Phase { get; private set; }

    /// <summary>The control the pick is for (its ImGui id).</summary>
    internal uint Owner { get; private set; }

    /// <summary>The color under the pointer, as last read; null before the first reading or where it can't be read.</summary>
    internal Vector3? Live { get; private set; }

    /// <summary>Why the spot under the pointer (or the spot picked) can't be read, or null.</summary>
    internal string? Problem { get; private set; }

    /// <summary>A pick is under way, or its last press is still held: the eyedropper takes the mouse and keyboard.</summary>
    internal bool ClaimsInput => Phase != EyedropperPhase.Idle;

    /// <summary>The preview by the pointer is shown (while pointing, never while the picked spot is read).</summary>
    internal bool ShowsPreview => Phase == EyedropperPhase.Sampling;

    /// <summary>Whether a pick for <paramref name="owner"/> is under way.</summary>
    internal bool IsPickingFor(uint owner) => Owner == owner && Phase is EyedropperPhase.Sampling or EyedropperPhase.Picking;

    /// <summary>Starts a pick for the control <paramref name="owner"/>; false while another is under way.</summary>
    internal bool Start(uint owner)
    {
        if (Phase != EyedropperPhase.Idle)
        {
            return false;
        }

        Phase = EyedropperPhase.Sampling;
        Owner = owner;
        armed = false;
        pickWasDown = true;
        cancelWasDown = true;
        pending = null;
        lastLiveMs = long.MinValue;
        Live = null;
        Problem = null;
        picked = null;
        reader.Begin();
        return true;
    }

    /// <summary>Ends any pick at once, with nothing changed (the plugin stopping).</summary>
    internal void Stop()
    {
        if (Phase is EyedropperPhase.Sampling or EyedropperPhase.Picking)
        {
            reader.End();
        }

        Phase = EyedropperPhase.Idle;
        pending = null;
        picked = null;
    }

    /// <summary>Once a frame: takes the readings that came back and what the player did.</summary>
    internal void Update(in EyedropperInput input)
    {
        if (picked is { } waiting && input.Frame - waiting.Frame > UnclaimedFrames)
        {
            picked = null;
        }

        if (Phase == EyedropperPhase.Idle)
        {
            return;
        }

        var pickPressed = input.PickButtonDown && !pickWasDown;
        var cancelPressed = input.CancelButtonDown && !cancelWasDown;
        pickWasDown = input.PickButtonDown;
        cancelWasDown = input.CancelButtonDown;

        TakeReadings(input.Frame);
        switch (Phase)
        {
            case EyedropperPhase.Sampling:
                Point(input, pickPressed, cancelPressed);
                break;
            case EyedropperPhase.Picking:
                Pick(input, cancelPressed);
                break;
            case EyedropperPhase.Ending when !input.PickButtonDown && !input.CancelButtonDown && !input.KeysDown:
                Phase = EyedropperPhase.Idle;
                break;
        }
    }

    /// <summary>
    /// For the control <paramref name="owner"/>, as it is drawn: the color it was given by a pick that
    /// just ended, once.
    /// </summary>
    internal bool TryTakePick(uint owner, out Vector3 color)
    {
        if (picked is { } done && done.Owner == owner)
        {
            picked = null;
            color = done.Color;
            return true;
        }

        color = default;
        return false;
    }

    private void Point(in EyedropperInput input, bool pickPressed, bool cancelPressed)
    {
        if (!input.PickButtonDown)
        {
            armed = true;
        }

        if (input.CancelKeyPressed || cancelPressed)
        {
            End();
            return;
        }

        if (input.PickKeyPressed || (armed && pickPressed))
        {
            if (input.Pointer is { } at)
            {
                Phase = EyedropperPhase.Picking;
                pickAt = at;
                pickFrame = input.Frame;
                pending = null;
                Problem = null;
            }
            else
            {
                Problem = NoPointer;
            }

            return;
        }

        if (pending is not null && input.Milliseconds - pendingSinceMs > ReadingTimeoutMs)
        {
            pending = null;
        }

        if (input.Pointer is { } pointer && pending is null && (lastLiveMs == long.MinValue || input.Milliseconds - lastLiveMs >= LivePeriodMs))
        {
            Ask(pointer, isPick: false, input.Milliseconds);
            lastLiveMs = input.Milliseconds;
        }
    }

    private void Pick(in EyedropperInput input, bool cancelPressed)
    {
        if (input.CancelKeyPressed || cancelPressed)
        {
            End();
            return;
        }

        if (pending is null)
        {
            if (input.Frame - pickFrame >= PickDelayFrames)
            {
                Ask(pickAt, isPick: true, input.Milliseconds);
            }
        }
        else if (input.Milliseconds - pendingSinceMs > ReadingTimeoutMs)
        {
            pending = null;
            Phase = EyedropperPhase.Sampling;
            Problem = NoAnswer;
        }
    }

    private void Ask(ScreenPixel at, bool isPick, long milliseconds)
    {
        pending = reader.Request(at);
        pendingIsPick = isPick;
        pendingSinceMs = milliseconds;
    }

    private void TakeReadings(int frame)
    {
        while (reader.TryTake(out var reading))
        {
            if (reading.Request != pending)
            {
                continue; // asked for before the pick, or given up on
            }

            pending = null;
            if (!pendingIsPick)
            {
                Live = reading.Color;
                Problem = reading.Color is null ? reading.Problem ?? Unreadable : null;
            }
            else if (reading.Color is { } color)
            {
                Live = color;
                Problem = null;
                picked = (Owner, color, frame);
                End();
            }
            else
            {
                // Nothing changes: the player can point elsewhere, or cancel.
                Phase = EyedropperPhase.Sampling;
                Problem = reading.Problem ?? Unreadable;
            }
        }
    }

    private void End()
    {
        reader.End();
        pending = null;
        Phase = EyedropperPhase.Ending;
    }

    // ---------------------------------------------------------------- pixels

    /// <summary>A Win32 COLORREF (0x00BBGGRR) as a color.</summary>
    internal static Vector3 FromColorRef(uint colorRef) =>
        new((colorRef & 0xFF) / 255f, ((colorRef >> 8) & 0xFF) / 255f, ((colorRef >> 16) & 0xFF) / 255f);

    /// <summary>
    /// The first pixel of raw image data in DXGI format <paramref name="dxgiFormat"/>, as a color: the
    /// 8-bit RGBA and BGRA formats (their sRGB forms hold the same bytes) and R10G10B10A2. Null for
    /// any other format, or too few bytes.
    /// </summary>
    internal static Vector3? FromRawPixel(int dxgiFormat, ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            return null;
        }

        switch (dxgiFormat)
        {
            case 28 or 29: // R8G8B8A8_UNORM(_SRGB)
                return new Vector3(data[0] / 255f, data[1] / 255f, data[2] / 255f);
            case 87 or 88 or 91 or 93: // B8G8R8A8_UNORM, B8G8R8X8_UNORM and their _SRGB forms
                return new Vector3(data[2] / 255f, data[1] / 255f, data[0] / 255f);
            case 24: // R10G10B10A2_UNORM
                var packed = BitConverter.ToUInt32(data[..4]);
                return new Vector3((packed & 0x3FF) / 1023f, ((packed >> 10) & 0x3FF) / 1023f, ((packed >> 20) & 0x3FF) / 1023f);
            default:
                return null;
        }
    }

    /// <summary>
    /// Where <paramref name="at"/> falls in a window's client area, whose corners are
    /// <paramref name="topLeft"/> (inside it) and <paramref name="bottomRight"/> (just past it), in
    /// screen pixels: 0 to 1 across each way, measured to the pixel's center. False outside it.
    /// </summary>
    internal static bool TryPlace(ScreenPixel at, ScreenPixel topLeft, ScreenPixel bottomRight, out Vector2 place)
    {
        var width = bottomRight.X - topLeft.X;
        var height = bottomRight.Y - topLeft.Y;
        if (width <= 0 || height <= 0 || at.X < topLeft.X || at.Y < topLeft.Y || at.X >= bottomRight.X || at.Y >= bottomRight.Y)
        {
            place = default;
            return false;
        }

        place = new Vector2((at.X - topLeft.X + 0.5f) / width, (at.Y - topLeft.Y + 0.5f) / height);
        return true;
    }

    /// <summary>The pixel of a <paramref name="width"/> by <paramref name="height"/> image at <paramref name="place"/> (see <see cref="TryPlace"/>).</summary>
    internal static (int X, int Y) PixelAt(Vector2 place, int width, int height) =>
        (Math.Clamp((int)(place.X * width), 0, Math.Max(0, width - 1)), Math.Clamp((int)(place.Y * height), 0, Math.Max(0, height - 1)));

    /// <summary>A color as the editors write it: "#RRGGBB".</summary>
    internal static string Hex(Vector3 color) =>
        $"#{Channel(color.X):X2}{Channel(color.Y):X2}{Channel(color.Z):X2}";

    private static int Channel(float value) => (int)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f);
}
