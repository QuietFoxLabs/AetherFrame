using System;
using System.Numerics;
using System.Runtime.InteropServices;
using AetherFrame.UI.Editor;

namespace AetherFrame.Services;

/// <summary>
/// The screen eyedropper's view of the desktop through Win32 (issue #120): where the pointer is, which
/// mouse buttons are down wherever it is, whether the game's window shows at a pixel, and the color
/// Windows shows at a pixel. Every position is in physical pixels of the virtual screen, read with the
/// calling thread made per-monitor DPI aware for the call (<see cref="PhysicalPixels"/>), so display
/// scaling and monitors of different scales agree with each other. Nothing here throws: a call
/// Windows refuses answers null or false.
/// </summary>
internal static class ScreenPixels
{
    private const int VkLButton = 0x01;
    private const int VkRButton = 0x02;
    private const int VkReturn = 0x0D;
    private const int VkEscape = 0x1B;
    private const int VkSpace = 0x20;
    private const int SmSwapButton = 23;
    private const uint GaRoot = 2;
    private const uint ClrInvalid = 0xFFFFFFFF;

    // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 (Windows 10, 1703 and later).
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    private static bool dpiContextMissing;

    /// <summary>Where the pointer is, or null.</summary>
    internal static ScreenPixel? Pointer()
    {
        try
        {
            using var physical = PhysicalPixels.Enter();
            return NativeMethods.GetCursorPos(out var point) ? new ScreenPixel(point.X, point.Y) : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the primary (<paramref name="primary"/>) or secondary mouse button is down, wherever the
    /// pointer is. Windows reports the physical buttons, so the two are swapped back for a player who
    /// swapped them.
    /// </summary>
    internal static bool ButtonDown(bool primary)
    {
        try
        {
            var swapped = NativeMethods.GetSystemMetrics(SmSwapButton) != 0;
            return IsDown(primary != swapped ? VkLButton : VkRButton);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Whether Enter, Space or Escape (the eyedropper's keys) is down, wherever the keyboard's focus is.</summary>
    internal static bool KeysDown()
    {
        try
        {
            return IsDown(VkReturn) || IsDown(VkSpace) || IsDown(VkEscape);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool IsDown(int virtualKey) => (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    /// <summary>
    /// Where <paramref name="at"/> falls in the game window's picture (0 to 1 across each way, see
    /// <see cref="Eyedropper.TryPlace"/>), when the game window is what shows there; false where
    /// another window, or a window of Dalamud's own beside the game, is on top, or off the game.
    /// </summary>
    internal static bool TryPlaceInGame(ScreenPixel at, IntPtr gameWindow, out Vector2 place)
    {
        place = default;
        if (gameWindow == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            using var physical = PhysicalPixels.Enter();
            var hit = NativeMethods.WindowFromPoint(new NativeMethods.Point(at.X, at.Y));
            if (hit == IntPtr.Zero || NativeMethods.GetAncestor(hit, GaRoot) != gameWindow
                || !NativeMethods.GetClientRect(gameWindow, out var client))
            {
                return false;
            }

            var topLeft = new NativeMethods.Point(0, 0);
            var bottomRight = new NativeMethods.Point(client.Right, client.Bottom);
            return NativeMethods.ClientToScreen(gameWindow, ref topLeft)
                && NativeMethods.ClientToScreen(gameWindow, ref bottomRight)
                && Eyedropper.TryPlace(at, new ScreenPixel(topLeft.X, topLeft.Y), new ScreenPixel(bottomRight.X, bottomRight.Y), out place);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// The color Windows shows at <paramref name="at"/> (the desktop as composed: every window, the
    /// game's included when it isn't in exclusive full screen), or null when Windows won't say. Can
    /// take a few milliseconds while Windows composes; any thread.
    /// </summary>
    internal static Vector3? ReadDesktop(ScreenPixel at)
    {
        try
        {
            using var physical = PhysicalPixels.Enter();
            var screen = NativeMethods.GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var color = NativeMethods.GetPixel(screen, at.X, at.Y);
                return color == ClrInvalid ? null : Eyedropper.FromColorRef(color);
            }
            finally
            {
                NativeMethods.ReleaseDC(IntPtr.Zero, screen);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Makes the calling thread per-monitor DPI aware until disposed, so the calls made meanwhile see
    /// physical pixels whatever the game declares. Does nothing where Windows has no such setting
    /// (before Windows 10 1703, or some Wine versions): positions are then as Windows gives them.
    /// </summary>
    private readonly ref struct PhysicalPixels
    {
        private readonly IntPtr previous;

        private PhysicalPixels(IntPtr previous) => this.previous = previous;

        internal static PhysicalPixels Enter()
        {
            if (dpiContextMissing)
            {
                return default;
            }

            try
            {
                return new PhysicalPixels(NativeMethods.SetThreadDpiAwarenessContext(PerMonitorAwareV2));
            }
            catch (EntryPointNotFoundException)
            {
                dpiContextMissing = true;
                return default;
            }
        }

        public void Dispose()
        {
            if (previous != IntPtr.Zero)
            {
                NativeMethods.SetThreadDpiAwarenessContext(previous);
            }
        }
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern short GetAsyncKeyState(int key);

        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern IntPtr WindowFromPoint(Point point);

        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern IntPtr GetAncestor(IntPtr window, uint flags);

        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetClientRect(IntPtr window, out Rect rect);

        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ClientToScreen(IntPtr window, ref Point point);

        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

        [DllImport("gdi32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint GetPixel(IntPtr deviceContext, int x, int y);

        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

        /// <summary>POINT.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct Point
        {
            internal int X;
            internal int Y;

            internal Point(int x, int y)
            {
                X = x;
                Y = y;
            }
        }

        /// <summary>RECT.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }
    }
}
