using System.Numerics;
using AetherFrame.UI.Theme;
using AetherFrame.UI.Tutorial;
using AetherFrame.Windows.Theme;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace AetherFrame.Windows.Tutorial;

/// <summary>
/// The first-run offer for a player new to AetherFrame: Start Tutorial, Maybe Later, or Do Not
/// Show Again, in a small centered window. Shown once the Library has loaded and only for an
/// install with no configuration and no Plates (see <see cref="FirstRunDetector"/>); closing it
/// without answering asks again next time, a few times at most. Never shown to a player
/// upgrading from an earlier version.
/// </summary>
internal sealed class FirstRunPromptWindow : Window
{
    private const ImGuiWindowFlags PromptFlags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings
        | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar;

    private readonly OnboardingCoordinator coordinator;
    private readonly ITutorialHost host;
    private readonly AetherWindowChrome chrome = new();
    private bool answered;

    internal FirstRunPromptWindow(OnboardingCoordinator coordinator, ITutorialHost host)
        : base("Welcome to AetherFrame##AetherFrameFirstRun", PromptFlags)
    {
        this.coordinator = coordinator;
        this.host = host;
        AllowPinning = false;
        AllowClickthrough = false;
    }

    /// <summary>Opens when the coordinator requests the offer.</summary>
    public override void PreOpenCheck()
    {
        if (coordinator.ConsumeOfferRequest())
        {
            answered = false;
            IsOpen = true;
            BringToFront();
        }
    }

    public override void PreDraw()
    {
        chrome.PushStyle();
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.WorkPos + (viewport.WorkSize / 2f), ImGuiCond.Appearing, new Vector2(0.5f));
        AetherWindowChrome.ApplyPolicy(this);
    }

    public override void PostDraw() => chrome.PopStyle();

    /// <summary>Closed without an answer (its close button, Escape): asked again next time, up to the limit.</summary>
    public override void OnClose()
    {
        if (!answered)
        {
            coordinator.DismissOffer();
        }
    }

    public override void Draw()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var width = 420f * scale;

        AetherBrand.Header("Welcome to AetherFrame", "Plates for your character");
        ImGui.Dummy(new Vector2(0f, AetherMetrics.SpaceSm * scale));

        using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + width))
        {
            using (ImRaii.PushColor(ImGuiCol.Text, AetherPalette.TextSecondary))
            {
                ImGui.TextWrapped("AetherFrame designs Plates: character cards that start from the familiar Adventure Plate and can grow into anything you like. Everything stays on your PC.");
                ImGui.Spacing();
                ImGui.TextWrapped("A short guided tour points at the real controls and walks you through creating, editing and saving your first Plate. It takes a few minutes, and you can leave it at any time.");
            }
        }

        ImGui.Dummy(new Vector2(0f, AetherMetrics.SpaceMd * scale));
        var buttonWidth = (width - (ImGui.GetStyle().ItemSpacing.X * 2f)) / 3f;
        if (AetherControls.PrimaryButton("Start Tutorial", new Vector2(buttonWidth, 0f), "Begin the guided tour now."))
        {
            Answer(FirstRunAnswer.StartTutorial);
        }

        ImGui.SameLine();
        if (AetherControls.SecondaryButton("Maybe Later", new Vector2(buttonWidth, 0f), "Not now. A quiet reminder stays in My Plates until you take the tour or dismiss it."))
        {
            Answer(FirstRunAnswer.MaybeLater);
        }

        ImGui.SameLine();
        if (AetherControls.GhostButton("Do Not Show Again", new Vector2(buttonWidth, 0f), "Never offer the tour. Help in My Plates can still start it."))
        {
            Answer(FirstRunAnswer.DoNotShowAgain);
        }
    }

    private void Answer(FirstRunAnswer answer)
    {
        answered = true;
        coordinator.AnswerOffer(answer, host.Snapshot());
        IsOpen = false;
    }
}
