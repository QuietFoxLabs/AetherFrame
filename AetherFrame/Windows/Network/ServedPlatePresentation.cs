using System;
using System.Globalization;
using System.Numerics;
using System.Threading.Tasks;
using AetherFrame.Services.Network.Sharing;
using AetherFrame.UI.Rendering;
using AetherFrame.UI.Theme;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;

namespace AetherFrame.Windows.Network;

/// <summary>
/// Another player's Plate in the Plate Viewer (N2-10, at the owner's request of October 1, 2026:
/// "the same view as My Plates, right-click a Plate, View"): floating over the game, sized to its
/// visual bounds, so artwork a Plate places past its edges shows, and moved, resized and closed as
/// any Plate there is. While it's being looked up, or there's nothing to show, the viewer says so.
/// Its right-click menu adds who it is, any notes, Refresh, Hide this player and Report. Every
/// name, World and Plate name is drawn only unformatted, never in an ImGui label (N7). It holds the
/// received images' textures only while it is presented. Compiled only in the networking preview
/// flavour.
/// </summary>
internal sealed class ServedPlatePresentation : IPlatePresentation, IDisposable
{
    private static readonly (string Reason, string Label)[] ReportReasons =
    [
        ("offensive", "Offensive"),
        ("impersonation", "Pretending to be someone else"),
        ("spam", "Spam or advertising"),
        ("other", "Something else"),
    ];

    private readonly PlateViewing viewing;
    private readonly ITextureProvider textures;
    private readonly ProfileRenderResources resources;
    private readonly Func<int, IDalamudTextureWrap?> imageOf;
    private Textures? shown;
    private string? problem;

    internal ServedPlatePresentation(PlateViewing viewing, ITextureProvider textures, ProfileRenderResources resources)
    {
        this.viewing = viewing ?? throw new ArgumentNullException(nameof(viewing));
        this.textures = textures ?? throw new ArgumentNullException(nameof(textures));
        this.resources = resources ?? throw new ArgumentNullException(nameof(resources));
        imageOf = index => shown?[index];
    }

    public string Message
    {
        get
        {
            var view = viewing.View;
            if (!viewing.CanView)
            {
                return view.Failure == ViewFailure.TakenOver ? FailureText(ViewFailure.TakenOver) + "\n\n" + PlateViewerWindow.NotSharing : PlateViewerWindow.NotSharing;
            }

            var who = view.Target is { } target ? target.Name + " (" + target.World + ")" : "That player";
            var text = view.Stage switch
            {
                ViewStage.Waiting or ViewStage.Looking => "Looking up " + who + "'s Plate...",
                ViewStage.NotFound => who + " has no AetherFrame Plate to show. They may not share, or may have paused or turned sharing off.",
                ViewStage.Hidden => "You hid " + who + "'s Plate on this PC, so it isn't looked up.",
                ViewStage.Failed => FailureText(view.Failure),
                _ => "Search for a player in AetherFrame Plates, or right-click their character in game and choose View AetherFrame Plate.",
            };
            return problem is null ? text : text + "\n\n" + problem;
        }
    }

    public string? MessageAction => !viewing.CanView ? null : viewing.View.Stage switch
    {
        ViewStage.Hidden => "Show their Plate again",
        ViewStage.Failed when viewing.View.Failure is not (ViewFailure.NotSharing or ViewFailure.TakenOver) => "Try again",
        _ => null,
    };

    /// <summary>Why a lookup failed, in words.</summary>
    internal static string FailureText(ViewFailure failure) => failure switch
    {
        ViewFailure.NotSharing => PlateViewerWindow.NotSharing,
        ViewFailure.TooMany => "You've looked up a lot of Plates in a short time. Try again in a little while.",
        ViewFailure.Unreachable => "AetherFrame's server couldn't be reached. Check your connection, then try again.",
        ViewFailure.KeyUnavailable => "Your shared character's key couldn't be opened on this PC. My Plates' Sharing window says why.",
        ViewFailure.TakenOver => "Another AetherFrame's Lodestone check took over your shared character, so it can't look Plates up from here. My Plates' Sharing window says what to do.",
        _ => "The server refused the lookup, or sent something AetherFrame doesn't show.",
    };

    public void RunMessageAction()
    {
        if (viewing.View.Stage == ViewStage.Hidden)
        {
            problem = viewing.Unhide() ? null : HideProblem();
        }
        else
        {
            problem = null;
            viewing.Refresh();
        }
    }

    public bool TryGetBounds(out CanvasBounds bounds, out Vector2 canvasSize)
    {
        // Viewing is part of sharing (V1): once no character shares, nothing received stays shown.
        var view = viewing.View;
        if (viewing.CanView && view.Stage == ViewStage.Shown && view.Plate is { } viewed)
        {
            if (!ReferenceEquals(shown?.Plate, viewed))
            {
                Release();
                shown = new Textures(textures, viewed);
            }

            bounds = new CanvasBounds(viewed.Plate.VisualMin, viewed.Plate.VisualMax);
            canvasSize = viewed.Plate.Canvas;
            return true;
        }

        Release();
        bounds = default;
        canvasSize = default;
        return false;
    }

    public void Draw(ImDrawListPtr drawList, Vector2 canvasOrigin, float scale, Vector2 clipMin, Vector2 clipMax)
    {
        if (shown is { } current)
        {
            // As the Plate Viewer draws a local Plate: no workspace backdrop, so the game shows through.
            ServedPlatePainter.Draw(drawList, current.Plate.Plate, canvasOrigin, scale, clipMin, clipMax, resources, imageOf, drawBackdrop: false);
        }
    }

    public void DrawMenuItems()
    {
        var view = viewing.View;
        if (view.Target is not { } target || view.Plate is not { } viewed)
        {
            return;
        }

        ImGui.TextUnformatted(target.Name);
        using (ImRaii.PushColor(ImGuiCol.Text, AetherPalette.TextMuted))
        {
            ImGui.TextUnformatted(target.World);
            ImGui.TextUnformatted(viewed.Plate.Name);
            foreach (var note in Notes(viewed))
            {
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + (280f * Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale));
                ImGui.TextUnformatted(note);
                ImGui.PopTextWrapPos();
            }
        }

        ImGui.Separator();
        if (ImGui.MenuItem("Refresh"))
        {
            problem = null;
            viewing.Refresh();
        }

        if (ImGui.MenuItem("Hide this player"))
        {
            problem = viewing.Hide() ? null : HideProblem();
        }

        switch (view.Report)
        {
            case ReportStage.Sending:
                ImGui.MenuItem("Sending the report...", string.Empty, false, false);
                break;

            case ReportStage.Sent:
                ImGui.MenuItem("Reported. Thank you.", string.Empty, false, false);
                break;

            default:
                using (var report = ImRaii.Menu(view.Report == ReportStage.Failed ? "Report (it didn't send: try again)" : "Report"))
                {
                    if (report.Success)
                    {
                        foreach (var (reason, label) in ReportReasons)
                        {
                            if (ImGui.MenuItem(label))
                            {
                                viewing.Report(reason);
                            }
                        }
                    }
                }

                break;
        }
    }

    public void Released()
    {
        problem = null;
        Release();
        viewing.Close();
    }

    public void Dispose() => Release();

    private static System.Collections.Generic.List<string> Notes(ViewedPlate viewed)
    {
        var notes = new System.Collections.Generic.List<string>(viewed.Plate.Notes);
        if (viewed.ImagesRefused > 0)
        {
            notes.Add(viewed.ImagesRefused.ToString(CultureInfo.InvariantCulture) + (viewed.ImagesRefused == 1 ? " image didn't pass AetherFrame's checks, so it isn't shown." : " images didn't pass AetherFrame's checks, so they aren't shown."));
        }

        if (viewed.ImagesMissing > 0)
        {
            notes.Add(viewed.ImagesMissing.ToString(CultureInfo.InvariantCulture) + (viewed.ImagesMissing == 1 ? " image" : " images") + " couldn't be loaded: the Plate may have changed while it loaded. Refresh to see it as it is now.");
        }

        return notes;
    }

    private string HideProblem() => viewing.Hidden.Unreadable
        ? "AetherFrame's list of hidden players (" + HiddenPlates.FileName + " in its configuration folder) can't be read, so nobody can be hidden or shown again until it's fixed or moved aside."
        : "AetherFrame couldn't save its list of hidden players, so nothing changed. Try again.";

    private void Release()
    {
        if (shown is { } released)
        {
            ServedPlatePainter.Forget(released.Plate.Plate);
            released.Dispose();
        }

        shown = null;
    }

    /// <summary>The received images of one Plate, decoded for display; each is drawn once it has loaded, and none after <see cref="Dispose"/>.</summary>
    private sealed class Textures : IDisposable
    {
        private readonly IDalamudTextureWrap?[] wraps;
        private bool disposed;

        internal Textures(ITextureProvider provider, ViewedPlate plate)
        {
            Plate = plate;
            wraps = new IDalamudTextureWrap?[plate.Images.Count];
            for (var index = 0; index < wraps.Length; index++)
            {
                if (plate.Images[index] is { } bytes)
                {
                    _ = LoadAsync(provider, bytes, index);
                }
            }
        }

        internal ViewedPlate Plate { get; }

        internal IDalamudTextureWrap? this[int index]
        {
            get
            {
                lock (wraps)
                {
                    return index >= 0 && index < wraps.Length ? wraps[index] : null;
                }
            }
        }

        public void Dispose()
        {
            lock (wraps)
            {
                disposed = true;
                for (var index = 0; index < wraps.Length; index++)
                {
                    wraps[index]?.Dispose();
                    wraps[index] = null;
                }
            }
        }

        private async Task LoadAsync(ITextureProvider provider, byte[] bytes, int index)
        {
            try
            {
                var wrap = await provider.CreateFromImageAsync(bytes, "AetherFrame viewer").ConfigureAwait(false);
                lock (wraps)
                {
                    if (disposed)
                    {
                        wrap.Dispose();
                        return;
                    }

                    wraps[index] = wrap;
                }
            }
            catch (Exception)
            {
                // Checked before it got here; one the game can't decode just isn't drawn.
            }
        }
    }
}
