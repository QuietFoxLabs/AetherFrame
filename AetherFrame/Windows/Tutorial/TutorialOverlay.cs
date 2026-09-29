using System.Collections.Generic;
using AetherFrame.UI.Tutorial;
using Dalamud.Interface.Windowing;

namespace AetherFrame.Windows.Tutorial;

/// <summary>
/// The tutorial's windows, in the order they must be added to the window system after every
/// other AetherFrame window: the driver (computes the frame's overlay from the anchors those
/// windows marked), the five shades, the card, and the first-run offer. Shades and card open
/// and close with the tutorial; the plugin asks <see cref="Update"/> once per frame for that.
/// </summary>
internal sealed class TutorialOverlay
{
    private readonly OnboardingCoordinator coordinator;
    private readonly TutorialOverlayFrame frame = new();
    private readonly TutorialOverlayWindow driver;
    private readonly TutorialShadeWindow[] shades;
    private readonly TutorialCardWindow card;
    private readonly FirstRunPromptWindow offer;
    private bool wasActive;

    /// <param name="coordinator">The tutorial's state.</param>
    /// <param name="host">What the tutorial may see and do.</param>
    /// <param name="dimmedWindows">AetherFrame's own windows: what the dim covers.</param>
    internal TutorialOverlay(OnboardingCoordinator coordinator, ITutorialHost host, IReadOnlyList<Window> dimmedWindows)
    {
        this.coordinator = coordinator;
        driver = new TutorialOverlayWindow(coordinator, host, frame, dimmedWindows);
        shades = new TutorialShadeWindow[TutorialShadeWindow.HoleCoverIndex + 1];
        for (var i = 0; i < shades.Length; i++)
        {
            shades[i] = new TutorialShadeWindow(frame, i);
        }

        card = new TutorialCardWindow(coordinator, host, frame);
        offer = new FirstRunPromptWindow(coordinator, host);
    }

    /// <summary>The windows, in the order to add them.</summary>
    internal IEnumerable<Window> Windows
    {
        get
        {
            yield return driver;
            foreach (var shade in shades)
            {
                yield return shade;
            }

            yield return card;
            yield return offer;
        }
    }

    /// <summary>
    /// Once per frame, before the window system draws: the shades and the card open when the
    /// tutorial starts and close when it ends. The card is opened only on that transition, so a
    /// card Dalamud closed after a fault stays closed (its OnClose stops the tour) instead of being
    /// reopened into the same fault every frame; a shade's Draw does nothing that can fault, so a
    /// shade closed by anything else is simply reopened.
    /// </summary>
    internal void Update()
    {
        var active = coordinator.IsTutorialActive;
        if (active != wasActive)
        {
            wasActive = active;
            card.IsOpen = active;
        }

        foreach (var shade in shades)
        {
            shade.IsOpen = active;
        }
    }
}
