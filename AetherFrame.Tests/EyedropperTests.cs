using System;
using System.Collections.Generic;
using System.Numerics;
using AetherFrame.UI.Editor;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The screen eyedropper's rules (issue #120): it reads nothing until a pick starts, shows the color
/// under the pointer as it moves, picks the color at the very spot clicked (or pointed at with Enter),
/// gives it to the control that started the pick and to no other, and ends with nothing changed on
/// Escape, the secondary button, or a spot that can't be read. The press that ends a pick is kept
/// until it is let go.
/// </summary>
public class EyedropperTests
{
    private const uint Owner = 7;
    private static readonly ScreenPixel Red = new(10, 10);
    private static readonly ScreenPixel Blue = new(-1500, 300); // a monitor left of the primary one
    private static readonly ScreenPixel Protected = new(50, 50);

    private static readonly Vector3 RedColor = new(1f, 0f, 0f);
    private static readonly Vector3 BlueColor = new(0f, 0f, 1f);

    private sealed class FakeScreen : IScreenColorReader
    {
        private readonly Queue<ScreenReading> ready = new();
        private readonly List<(int Id, ScreenPixel At)> held = new();
        private int next;

        internal List<ScreenPixel> Asked { get; } = new();

        internal int Begins { get; private set; }

        internal int Ends { get; private set; }

        /// <summary>False: readings wait for <see cref="Answer"/>, as the game's picture's do.</summary>
        internal bool AnswersAtOnce { get; set; } = true;

        public void Begin() => Begins++;

        public int Request(ScreenPixel at)
        {
            var id = ++next;
            Asked.Add(at);
            if (AnswersAtOnce)
            {
                ready.Enqueue(Read(id, at));
            }
            else
            {
                held.Add((id, at));
            }

            return id;
        }

        internal void Answer()
        {
            foreach (var (id, at) in held)
            {
                ready.Enqueue(Read(id, at));
            }

            held.Clear();
        }

        public bool TryTake(out ScreenReading reading) => ready.TryDequeue(out reading);

        public void End() => Ends++;

        private static ScreenReading Read(int id, ScreenPixel at) =>
            at == Red ? new ScreenReading(id, RedColor, null)
            : at == Blue ? new ScreenReading(id, BlueColor, null)
            : new ScreenReading(id, null, "Protected window");
    }

    /// <summary>Frames a pick, 16 ms apart, with the player's input.</summary>
    private sealed class Player
    {
        private readonly Eyedropper eyedropper;
        private int frame = 100;
        private long milliseconds = 10_000;

        internal Player(Eyedropper eyedropper) => this.eyedropper = eyedropper;

        internal int Frame => frame;

        internal void Frames(int count, ScreenPixel? pointer = null, bool button = false, bool secondary = false, bool keysDown = false)
        {
            for (var i = 0; i < count; i++)
            {
                Step(new EyedropperInput(frame, milliseconds, pointer ?? Red, button, secondary, false, false, keysDown));
            }
        }

        internal void Click(ScreenPixel at)
        {
            Frames(1, at, button: true);
            Frames(1, at, button: false);
        }

        internal void Escape(ScreenPixel? at = null) =>
            Step(new EyedropperInput(frame, milliseconds, at ?? Red, false, false, PickKeyPressed: false, CancelKeyPressed: true, KeysDown: true));

        internal void Enter(ScreenPixel? at) =>
            Step(new EyedropperInput(frame, milliseconds, at, false, false, PickKeyPressed: true, CancelKeyPressed: false, KeysDown: true));

        internal void Wait(long ms) => milliseconds += ms;

        private void Step(EyedropperInput input)
        {
            eyedropper.Update(input);
            frame++;
            milliseconds += 16;
        }
    }

    private static (Eyedropper Eyedropper, FakeScreen Screen, Player Player) Started()
    {
        var screen = new FakeScreen();
        var eyedropper = new Eyedropper(screen);
        var player = new Player(eyedropper);
        Assert.True(eyedropper.Start(Owner));
        return (eyedropper, screen, player);
    }

    /// <summary>Clicks <paramref name="at"/> (a fresh press) and lets the pick finish.</summary>
    private static void PickAt(Player player, ScreenPixel at)
    {
        player.Frames(1, at, button: false);
        player.Frames(1, at, button: true);
        player.Frames(Eyedropper.PickDelayFrames + 2, at, button: true);
        player.Frames(1, at, button: false);
    }

    // ---- reading only while picking ---------------------------------------------------------------

    [Fact]
    public void NothingIsRead_UntilAPickStarts()
    {
        var screen = new FakeScreen();
        var eyedropper = new Eyedropper(screen);

        new Player(eyedropper).Frames(30, Red, button: true);

        Assert.Equal(0, screen.Begins);
        Assert.Empty(screen.Asked);
        Assert.False(eyedropper.ClaimsInput);
    }

    [Fact]
    public void Pointing_ShowsTheColorUnderThePointer_ReadAFewTimesASecondAtMost()
    {
        var (eyedropper, screen, player) = Started();

        player.Frames(2, Red);
        Assert.Equal(RedColor, eyedropper.Live);
        Assert.True(eyedropper.ShowsPreview);
        Assert.True(eyedropper.IsPickingFor(Owner));

        // Twenty frames at 16 ms: about 320 ms, so 5 or 6 readings, never one a frame.
        player.Frames(20, Red);
        Assert.InRange(screen.Asked.Count, 5, 7);

        player.Frames(6, Blue);
        Assert.Equal(BlueColor, eyedropper.Live);
        Assert.Equal(1, screen.Begins);
    }

    // ---- picking --------------------------------------------------------------------------------

    [Fact]
    public void AClick_PicksTheColorAtTheSpotClicked_NotTheLastOneShown()
    {
        var (eyedropper, screen, player) = Started();
        screen.AnswersAtOnce = false;
        player.Frames(1, Red);
        screen.Answer();
        player.Frames(1, Red);
        Assert.Equal(RedColor, eyedropper.Live); // shown

        player.Frames(1, Blue, button: true); // clicked straight away on blue
        Assert.Equal(EyedropperPhase.Picking, eyedropper.Phase);
        Assert.False(eyedropper.ShowsPreview); // out of the way before the spot is read

        player.Frames(Eyedropper.PickDelayFrames, Blue, button: true);
        Assert.Equal(Blue, screen.Asked[^1]); // read at the spot clicked
        screen.Answer();
        player.Frames(1, Blue, button: true);

        Assert.True(eyedropper.TryTakePick(Owner, out var color));
        Assert.Equal(BlueColor, color);
        Assert.False(eyedropper.TryTakePick(Owner, out _)); // once
        Assert.Equal(1, screen.Ends);
    }

    [Fact]
    public void ThePressThatPicked_IsKeptUntilLetGo()
    {
        var (eyedropper, _, player) = Started();
        PickAt(player, Blue);
        Assert.Equal(EyedropperPhase.Idle, eyedropper.Phase);

        var (held, _, holding) = Started();
        holding.Frames(1, Blue);
        holding.Frames(1, Blue, button: true);
        holding.Frames(Eyedropper.PickDelayFrames + 2, Blue, button: true);
        Assert.Equal(EyedropperPhase.Ending, held.Phase);
        Assert.True(held.ClaimsInput); // the release reaches nothing under the pointer
        holding.Frames(1, Blue, button: false);
        Assert.False(held.ClaimsInput);
    }

    [Fact]
    public void TheClickThatStartedThePick_IsNotAPick()
    {
        var (eyedropper, _, player) = Started();

        player.Frames(10, Red, button: true); // still held from the eyedropper button
        Assert.Equal(EyedropperPhase.Sampling, eyedropper.Phase);

        player.Frames(1, Red, button: false);
        player.Frames(1, Red, button: true);
        Assert.Equal(EyedropperPhase.Picking, eyedropper.Phase);
    }

    [Fact]
    public void Enter_PicksWhereThePointerIs_WithoutAClick()
    {
        var (eyedropper, _, player) = Started();
        player.Frames(2, Blue);

        player.Enter(Blue);
        player.Frames(Eyedropper.PickDelayFrames + 2, Blue, keysDown: true);

        Assert.True(eyedropper.TryTakePick(Owner, out var color));
        Assert.Equal(BlueColor, color);
        Assert.True(eyedropper.ClaimsInput); // Enter still held
        player.Frames(1, Blue);
        Assert.False(eyedropper.ClaimsInput);
    }

    [Fact]
    public void ThePickedColor_GoesOnlyToTheControlThatAskedForIt_AndNotLate()
    {
        var (eyedropper, _, player) = Started();
        PickAt(player, Red);

        Assert.False(eyedropper.TryTakePick(Owner + 1, out _));

        // A control no longer drawn never takes it; a later one can't find it.
        player.Frames(Eyedropper.UnclaimedFrames + 1, Red);
        Assert.False(eyedropper.TryTakePick(Owner, out _));
    }

    [Fact]
    public void OnePickAtATime()
    {
        var (eyedropper, _, player) = Started();
        Assert.False(eyedropper.Start(Owner + 1));

        player.Escape();
        Assert.False(eyedropper.Start(Owner + 1)); // Escape still held
        player.Frames(1, Red);
        Assert.True(eyedropper.Start(Owner + 1));
    }

    // ---- canceling ------------------------------------------------------------------------------

    [Fact]
    public void Escape_EndsThePick_WithNothingPicked()
    {
        var (eyedropper, screen, player) = Started();
        player.Frames(3, Red);

        player.Escape();

        Assert.Equal(EyedropperPhase.Ending, eyedropper.Phase);
        Assert.True(eyedropper.ClaimsInput); // Escape reaches neither the game nor a window's close
        player.Frames(1, Red, keysDown: true);
        Assert.True(eyedropper.ClaimsInput);
        player.Frames(1, Red);
        Assert.Equal(EyedropperPhase.Idle, eyedropper.Phase);
        Assert.False(eyedropper.TryTakePick(Owner, out _));
        Assert.Equal(1, screen.Ends);
    }

    [Fact]
    public void TheSecondaryButton_EndsThePick_WithNothingPicked()
    {
        var (eyedropper, _, player) = Started();
        player.Frames(2, Red);

        player.Frames(1, Red, secondary: true);
        Assert.Equal(EyedropperPhase.Ending, eyedropper.Phase);
        player.Frames(1, Red);

        Assert.Equal(EyedropperPhase.Idle, eyedropper.Phase);
        Assert.False(eyedropper.TryTakePick(Owner, out _));
    }

    [Fact]
    public void Escape_WhileThePickedSpotIsRead_StillCancels()
    {
        var (eyedropper, screen, player) = Started();
        screen.AnswersAtOnce = false;
        player.Frames(1, Blue);
        player.Frames(1, Blue, button: true);
        Assert.Equal(EyedropperPhase.Picking, eyedropper.Phase);
        player.Frames(Eyedropper.PickDelayFrames, Blue, button: false);

        player.Escape(Blue);
        screen.Answer(); // the reading comes back after all
        player.Frames(2, Blue);

        Assert.False(eyedropper.TryTakePick(Owner, out _));
        Assert.Equal(EyedropperPhase.Idle, eyedropper.Phase);
    }

    [Fact]
    public void Stop_EndsAnyPick_WithNothingPicked()
    {
        var (eyedropper, screen, player) = Started();
        player.Frames(2, Red);

        eyedropper.Stop();

        Assert.Equal(EyedropperPhase.Idle, eyedropper.Phase);
        Assert.Equal(1, screen.Ends);
        Assert.False(eyedropper.TryTakePick(Owner, out _));
    }

    // ---- spots that can't be read ---------------------------------------------------------------

    [Fact]
    public void ASpotThatCantBeRead_SaysWhy_AndPicksNothing()
    {
        var (eyedropper, _, player) = Started();
        player.Frames(2, Protected);
        Assert.Null(eyedropper.Live);
        Assert.Equal("Protected window", eyedropper.Problem);

        PickAt(player, Protected);

        Assert.Equal(EyedropperPhase.Sampling, eyedropper.Phase); // still pointing: try elsewhere, or cancel
        Assert.Equal("Protected window", eyedropper.Problem);
        Assert.False(eyedropper.TryTakePick(Owner, out _));

        PickAt(player, Red);
        Assert.True(eyedropper.TryTakePick(Owner, out var color));
        Assert.Equal(RedColor, color);
    }

    [Fact]
    public void AReadingThatNeverComes_IsGivenUpOn()
    {
        var (eyedropper, screen, player) = Started();
        screen.AnswersAtOnce = false;
        player.Frames(1, Red, button: false);
        player.Frames(1, Red, button: true);
        player.Frames(Eyedropper.PickDelayFrames + 1, Red, button: false);

        player.Wait(Eyedropper.ReadingTimeoutMs + 1);
        player.Frames(1, Red);

        Assert.Equal(EyedropperPhase.Sampling, eyedropper.Phase);
        Assert.Equal(Eyedropper.NoAnswer, eyedropper.Problem);

        // Pointing carries on: a new reading is asked for.
        var asked = screen.Asked.Count;
        player.Wait(Eyedropper.ReadingTimeoutMs + 1);
        player.Frames(1, Red);
        Assert.True(screen.Asked.Count > asked);
    }

    [Fact]
    public void APickWithNoPointer_SaysSo_AndWaits()
    {
        var (eyedropper, _, player) = Started();
        player.Frames(1, Red);

        player.Enter(at: null);

        Assert.Equal(EyedropperPhase.Sampling, eyedropper.Phase);
        Assert.Equal(Eyedropper.NoPointer, eyedropper.Problem);
    }

    // ---- pixels ---------------------------------------------------------------------------------

    [Fact]
    public void ColorRefs_AreBlueGreenRed()
    {
        Assert.Equal(new Vector3(0x99 / 255f, 0x66 / 255f, 0x33 / 255f), Eyedropper.FromColorRef(0x00336699));
        Assert.Equal("#996633", Eyedropper.Hex(Eyedropper.FromColorRef(0x00336699)));
    }

    [Theory]
    [InlineData(28)] // R8G8B8A8_UNORM
    [InlineData(29)] // R8G8B8A8_UNORM_SRGB
    public void RgbaPixels_ReadInOrder(int format) =>
        Assert.Equal(new Vector3(0x12 / 255f, 0x34 / 255f, 0x56 / 255f), Eyedropper.FromRawPixel(format, new byte[] { 0x12, 0x34, 0x56, 0xFF }));

    [Theory]
    [InlineData(87)] // B8G8R8A8_UNORM
    [InlineData(88)] // B8G8R8X8_UNORM
    [InlineData(91)] // B8G8R8A8_UNORM_SRGB
    [InlineData(93)] // B8G8R8X8_UNORM_SRGB
    public void BgraPixels_ReadBackwards(int format) =>
        Assert.Equal(new Vector3(0x56 / 255f, 0x34 / 255f, 0x12 / 255f), Eyedropper.FromRawPixel(format, new byte[] { 0x12, 0x34, 0x56, 0xFF }));

    [Fact]
    public void TenBitPixels_Read()
    {
        // R 1023, G 0, B 512, A 3.
        var packed = 1023u | (0u << 10) | (512u << 20) | (3u << 30);
        var color = Eyedropper.FromRawPixel(24, BitConverter.GetBytes(packed))!.Value;

        Assert.Equal(1f, color.X);
        Assert.Equal(0f, color.Y);
        Assert.Equal(512f / 1023f, color.Z, 5);
    }

    [Fact]
    public void OtherFormats_AndShortData_AreNotGuessed()
    {
        Assert.Null(Eyedropper.FromRawPixel(10, new byte[8])); // R16G16B16A16_FLOAT (HDR)
        Assert.Null(Eyedropper.FromRawPixel(87, new byte[3]));
    }

    [Fact]
    public void APixel_IsPlacedInAWindow_ByItsCenter()
    {
        var topLeft = new ScreenPixel(-1920, 0);
        var bottomRight = new ScreenPixel(0, 1080);

        Assert.True(Eyedropper.TryPlace(new ScreenPixel(-1920, 0), topLeft, bottomRight, out var first));
        Assert.Equal(new Vector2(0.5f / 1920f, 0.5f / 1080f), first);
        Assert.True(Eyedropper.TryPlace(new ScreenPixel(-1, 1079), topLeft, bottomRight, out var last));
        Assert.Equal((1919, 1079), Eyedropper.PixelAt(last, 1920, 1080));

        Assert.False(Eyedropper.TryPlace(new ScreenPixel(0, 500), topLeft, bottomRight, out _)); // just past the edge
        Assert.False(Eyedropper.TryPlace(new ScreenPixel(-1921, 500), topLeft, bottomRight, out _));
        Assert.False(Eyedropper.TryPlace(new ScreenPixel(5, 5), new ScreenPixel(10, 10), new ScreenPixel(10, 20), out _)); // no width
    }

    [Fact]
    public void APlace_MapsOntoAPictureOfAnotherSize()
    {
        // A window 1024 px wide whose picture is 2048 px (or the other way round): the same spot.
        Assert.True(Eyedropper.TryPlace(new ScreenPixel(512, 256), new ScreenPixel(0, 0), new ScreenPixel(1024, 512), out var place));
        Assert.Equal((1025, 513), Eyedropper.PixelAt(place, 2048, 1024));
        Assert.Equal((256, 128), Eyedropper.PixelAt(place, 512, 256));
        Assert.Equal((0, 0), Eyedropper.PixelAt(new Vector2(-0.2f, 2f), 1, 1));
    }
}
