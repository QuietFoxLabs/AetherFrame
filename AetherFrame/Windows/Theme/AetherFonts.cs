using System;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;

namespace AetherFrame.Windows.Theme;

/// <summary>
/// AetherFrame's typography: three sizes of the game's own Axis face over Dalamud's default
/// body font. Axis is what FFXIV's interface is set in, so a heading in it reads as part of the
/// game rather than a developer tool; the body stays Dalamud's default so it matches every
/// other plugin's text size and the player's own scale settings.
///
/// <list type="bullet">
/// <item><b>Display</b> (Axis 18 pt): a window's title row, the tutorial card's heading, an empty state's title.</item>
/// <item><b>Heading</b> (Axis 14 pt): a panel or section heading, a card's name.</item>
/// <item><b>Label</b> (Axis 12 pt): the small uppercase section labels and pills.</item>
/// </list>
///
/// The handles are built asynchronously by Dalamud; until one is available, pushing it pushes
/// nothing and text draws in the default font, so nothing ever waits on a font. Created once by
/// the plugin and disposed with it.
/// </summary>
internal sealed class AetherFonts : IDisposable
{
    private static AetherFonts? current;

    private readonly IFontHandle display;
    private readonly IFontHandle heading;
    private readonly IFontHandle label;
    private bool disposed;

    internal AetherFonts(IFontAtlas atlas)
    {
        display = atlas.NewGameFontHandle(new GameFontStyle(GameFontFamilyAndSize.Axis18));
        heading = atlas.NewGameFontHandle(new GameFontStyle(GameFontFamilyAndSize.Axis14));
        label = atlas.NewGameFontHandle(new GameFontStyle(GameFontFamilyAndSize.Axis12));
    }

    /// <summary>The plugin's fonts, once created; null before that and after disposal (every push is then a no-op).</summary>
    internal static AetherFonts? Current
    {
        get => current;
        set => current = value;
    }

    /// <summary>Pushes the display face for a <c>using</c> block (no-op when unavailable).</summary>
    internal static IDisposable Display() => Push(current?.display);

    /// <summary>Pushes the heading face for a <c>using</c> block (no-op when unavailable).</summary>
    internal static IDisposable Heading() => Push(current?.heading);

    /// <summary>Pushes the small label face for a <c>using</c> block (no-op when unavailable).</summary>
    internal static IDisposable Label() => Push(current?.label);

    private static IDisposable Push(IFontHandle? handle)
    {
        if (handle is null)
        {
            return NoFont.Instance;
        }

        try
        {
            return handle.Available ? handle.Push() : NoFont.Instance;
        }
        catch (ObjectDisposedException)
        {
            return NoFont.Instance;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (ReferenceEquals(current, this))
        {
            current = null;
        }

        display.Dispose();
        heading.Dispose();
        label.Dispose();
    }

    private sealed class NoFont : IDisposable
    {
        internal static readonly NoFont Instance = new();

        public void Dispose()
        {
        }
    }
}
