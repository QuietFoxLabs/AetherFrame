using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Services.Diagnostics;
using AetherFrame.UI.Editor;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

namespace AetherFrame.Services;

/// <summary>
/// The screen eyedropper's reader (issue #120), from two sources:
/// <list type="bullet">
/// <item>where the game's window shows, the game's own picture, Dalamud's interface included (so
/// the Plate in an editor too), through Dalamud's supported capture of the game's viewport
/// (<see cref="ITextureProvider.CreateFromImGuiViewportAsync"/>, kept up to date only while a pick
/// is under way) and its texture readback, cut down to the one pixel. This works in every display
/// mode the game has, exclusive full screen included;</item>
/// <item>everywhere else (other windows and monitors, and Dalamud windows moved out of the game's),
/// the desktop as Windows composes it (<see cref="ScreenPixels.ReadDesktop"/>), off the render
/// thread. Where the game's capture can't be had, the game's window is read this way too.</item>
/// </list>
/// Readings come back on any thread and wait in a queue for the render thread.
/// </summary>
internal sealed class DalamudScreenColorReader : IScreenColorReader
{
    internal const string GameUnreadable = "The game's picture can't be read here. Try again, or press Escape.";
    internal const string GameFormat = "The game's picture is in a color format this can't read (HDR?).";
    internal const string DesktopUnreadable = "Windows didn't let AetherFrame read this spot (it can't read a game in exclusive Full Screen, or some protected windows).";
    internal const string GettingReady = "Getting the game's picture ready...";

    private readonly ITextureProvider textures;
    private readonly ITextureReadbackProvider readback;
    private readonly Func<IntPtr> gameWindow;
    private readonly IAetherFrameLog log;
    private readonly ConcurrentQueue<ScreenReading> readings = new();
    private readonly List<Task> running = new();

    private Task<IDalamudTextureWrap>? capture;
    private CancellationTokenSource? session;
    private int next;
    private bool captureFailureLogged;

    internal DalamudScreenColorReader(ITextureProvider textures, ITextureReadbackProvider readback, Func<IntPtr> gameWindow, IAetherFrameLog log)
    {
        this.textures = textures ?? throw new ArgumentNullException(nameof(textures));
        this.readback = readback ?? throw new ArgumentNullException(nameof(readback));
        this.gameWindow = gameWindow ?? throw new ArgumentNullException(nameof(gameWindow));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public void Begin()
    {
        End();
        session = new CancellationTokenSource();
        captureFailureLogged = false;
        try
        {
            capture = textures.CreateFromImGuiViewportAsync(
                new ImGuiViewportTextureArgs
                {
                    ViewportId = ImGui.GetMainViewport().ID,
                    AutoUpdate = true,
                    TakeBeforeImGuiRender = false,
                    KeepTransparency = false,
                },
                "AetherFrame eyedropper",
                session.Token);
        }
        catch (Exception e)
        {
            capture = Task.FromException<IDalamudTextureWrap>(e);
        }
    }

    public int Request(ScreenPixel at)
    {
        var id = ++next;
        if (session is not { } current)
        {
            readings.Enqueue(new ScreenReading(id, null, GameUnreadable));
            return id;
        }

        running.RemoveAll(task => task.IsCompleted);
        if (ScreenPixels.TryPlaceInGame(at, gameWindow(), out var place) && capture is { } game && !game.IsFaulted && !game.IsCanceled)
        {
            if (game.IsCompletedSuccessfully)
            {
                ReadGame(id, game.Result, place, current.Token);
            }
            else
            {
                readings.Enqueue(new ScreenReading(id, null, GettingReady));
            }

            return id;
        }

        if (capture is { IsFaulted: true } failed && !captureFailureLogged)
        {
            captureFailureLogged = true;
            var cause = failed.Exception?.GetBaseException();
            log.Warning($"Eyedropper: the game's picture couldn't be captured ({cause?.GetType().Name}, 0x{cause?.HResult ?? 0:X8}); reading the game's window from the desktop instead.");
        }

        ReadDesktop(id, at, current.Token);
        return id;
    }

    public bool TryTake(out ScreenReading reading) => readings.TryDequeue(out reading);

    public void End()
    {
        if (session is null)
        {
            return;
        }

        session.Cancel();
        session = null;

        // The capture goes once every readback that uses it is done.
        var game = capture;
        capture = null;
        var outstanding = running.ToArray();
        running.Clear();
        readings.Clear();
        if (game is not null)
        {
            _ = Task.WhenAll(outstanding.Append(game)).ContinueWith(
                all =>
                {
                    if (game.IsCompletedSuccessfully)
                    {
                        game.Result.Dispose();
                    }
                    else
                    {
                        _ = game.Exception; // observed: a capture that failed was read from the desktop instead
                    }

                    _ = all.Exception;
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private void ReadGame(int id, IDalamudTextureWrap picture, Vector2 place, CancellationToken cancellation)
    {
        var width = picture.Width;
        var height = picture.Height;
        if (width <= 0 || height <= 0)
        {
            readings.Enqueue(new ScreenReading(id, null, GettingReady));
            return;
        }

        var (x, y) = Eyedropper.PixelAt(place, width, height);
        var pixel = new TextureModificationArgs
        {
            Uv0 = new Vector2(x / (float)width, y / (float)height),
            Uv1 = new Vector2((x + 1) / (float)width, (y + 1) / (float)height),
            NewWidth = 1,
            NewHeight = 1,
            MakeOpaque = true,
        };

        Task<(RawImageSpecification Specification, byte[] RawData)> read;
        try
        {
            read = readback.GetRawImageAsync(picture, pixel, leaveWrapOpen: true, cancellation);
        }
        catch (Exception e)
        {
            read = Task.FromException<(RawImageSpecification, byte[])>(e);
        }

        running.Add(read.ContinueWith(
            task =>
            {
                if (task.IsCompletedSuccessfully)
                {
                    var color = Eyedropper.FromRawPixel(task.Result.Specification.DxgiFormat, task.Result.RawData);
                    readings.Enqueue(new ScreenReading(id, color, color is null ? GameFormat : null));
                }
                else
                {
                    _ = task.Exception; // observed: the reading says it failed
                    readings.Enqueue(new ScreenReading(id, null, GameUnreadable));
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default));
    }

    private void ReadDesktop(int id, ScreenPixel at, CancellationToken cancellation)
    {
        running.Add(Task.Run(
            () =>
            {
                Vector3? color;
                try
                {
                    color = ScreenPixels.ReadDesktop(at);
                }
                catch (Exception)
                {
                    color = null; // never left unobserved: the reading says it failed
                }

                readings.Enqueue(new ScreenReading(id, color, color is null ? DesktopUnreadable : null));
            },
            cancellation));
    }
}
