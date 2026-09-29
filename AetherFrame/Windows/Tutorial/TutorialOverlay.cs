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

    internal TutorialOverlay(OnboardingCoordinator coordinator, ITutorialHost host)
    {
        this.coordinator = coordinator;
        driver = new TutorialOverlayWindow(coordinator, host, frame);
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
    /// Once per frame, before the window system draws: the shades and the card are open exactly
    /// while the tutorial runs. (The driver is always open; the offer opens itself.)
    /// </summary>
    internal void Update()
    {
        var active = coordinator.IsTutorialActive;
        foreach (var shade in shades)
        {
            shade.IsOpen = active;
        }

        card.IsOpen = active;
    }
}
