using System;
using System.Numerics;
using AetherFrame.Services.Network.Publishing;
using AetherFrame.UI.Theme;
using AetherFrame.Windows.Theme;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;

namespace AetherFrame.Windows.Network;

/// <summary>
/// The preview's share check (N2-6c's second part), reached from a Plate's menu in My Plates: what
/// sharing the Plate would send, from a private copy of its saved state, or why it can't be shared.
/// It signs nothing and sends nothing (C3): sharing a character's Active Plate is turned on in the
/// Sharing window. Every text from the Plate is drawn unformatted, never inside an ImGui label, a
/// tooltip or a format string (N7). Compiled only in the networking preview flavour.
/// </summary>
internal sealed class ShareCheckWindow : Window, IDisposable
{
    private const string Intro =
        "The name, texts and images sharing this Plate would send, checked from its saved state, or why it can't be shared yet. " +
        "Nothing is signed or sent from here: to share a character's Active Plate, turn sharing on in the Sharing window.";

    private readonly ShareCheck check;
    private readonly CandidateView candidateView;
    private readonly AetherWindowChrome chrome = new();

    internal ShareCheckWindow(ShareCheck check, ITextureProvider textures)
        : base("Check what would be shared (preview)##AetherFrameShareCheck", ImGuiWindowFlags.NoCollapse)
    {
        this.check = check ?? throw new ArgumentNullException(nameof(check));
        candidateView = new CandidateView(textures);
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480f, 360f),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        RespectCloseHotkey = true;
    }

    /// <summary>Opens the window on <paramref name="plateId"/>'s saved state, checking it afresh.</summary>
    internal void Open(Guid plateId)
    {
        candidateView.Release();
        check.Begin(plateId);
        IsOpen = true;
    }

    public override void PreDraw()
    {
        chrome.PushStyle();
        AetherWindowChrome.ApplyPolicy(this);
    }

    public override void PostDraw() => chrome.PopStyle();

    public override void OnClose()
    {
        check.Reset();
        candidateView.Release();
    }

    public void Dispose()
    {
        check.Dispose();
        candidateView.Dispose();
    }

    public override void Draw()
    {
        check.OnFrame();
        var view = check.View;

        ImGui.PushTextWrapPos(0f);
        using (Dalamud.Interface.Utility.Raii.ImRaii.PushColor(ImGuiCol.Text, AetherPalette.TextMuted))
        {
            ImGui.TextUnformatted(Intro);
        }

        ImGui.PopTextWrapPos();
        AetherControls.Divider();

        AetherControls.Secondary("Plate");
        ImGui.TextUnformatted(view.PlateName.Length == 0 ? "(no name)" : view.PlateName);
        AetherControls.Divider();

        switch (view.Stage)
        {
            case ShareCheckStage.Resolving:
                AetherControls.StatusLine(AetherTone.Info, "Checking what it draws...");
                break;
            case ShareCheckStage.Preparing:
                AetherControls.StatusLine(AetherTone.Info, "Preparing its images...");
                break;
            case ShareCheckStage.Failed:
                AetherControls.Callout(AetherTone.Warning, ShareMessages.For(view.Failure));
                DrawCheckAgain(view);
                break;
            case ShareCheckStage.Refused:
                AetherControls.Callout(AetherTone.Danger, "This Plate can't be shared as it is. Change what is listed here, save it, and check again.");
                CandidateView.DrawProblems(view.Problems);
                DrawCheckAgain(view);
                break;
            case ShareCheckStage.Ready when view.Candidate is { } candidate:
                candidateView.Draw(candidate);
                AetherControls.Divider();
                DrawCheckAgain(view);
                break;
        }
    }

    private void DrawCheckAgain(ShareCheckView view)
    {
        if (view.PlateId != Guid.Empty && AetherControls.SecondaryButton("Check again"))
        {
            Open(view.PlateId);
        }
    }
}
