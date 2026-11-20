using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using BinanceTaskbar.Models;

namespace BinanceTaskbar.Services;

public readonly record struct NativeRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

public static class TaskbarDockService
{
    private const uint ABM_GETTASKBARPOS = 0x00000005;
    private const int GWL_EXSTYLE = -20;
    private const int GWLP_HWNDPARENT = -8;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_APPWINDOW = 0x00040000;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public NativeRect rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")]
    private static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    public static bool TryGetTaskbarRect(out NativeRect rect, out uint edge)
    {
        var data = new APPBARDATA
        {
            cbSize = Marshal.SizeOf<APPBARDATA>()
        };

        var result = SHAppBarMessage(ABM_GETTASKBARPOS, ref data);
        rect = data.rc;
        edge = data.uEdge;
        return result != UIntPtr.Zero && rect.Width > 0 && rect.Height > 0;
    }

    public static void DockToTaskbar(Window window, AppSettings settings)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !TryGetTaskbarRect(out var taskbar, out _))
        {
            return;
        }

        var scale = GetScale(handle);
        var windowWidth = (int)Math.Round(settings.Width * scale);
        var windowHeight = (int)Math.Round(settings.Height * scale);

        var x = taskbar.Left + (int)Math.Round(settings.LeftOffset * scale);
        x = Math.Clamp(x, taskbar.Left, Math.Max(taskbar.Left, taskbar.Right - windowWidth));

        var y = settings.TopOffset is { } topOffset
            ? taskbar.Top + (int)Math.Round(topOffset * scale)
            : taskbar.Top + Math.Max(0, (taskbar.Height - windowHeight) / 2);

        y = Math.Clamp(y, taskbar.Top, Math.Max(taskbar.Top, taskbar.Bottom - windowHeight));

        SetWindowPos(
            handle,
            HWND_TOPMOST,
            x,
            y,
            0,
            0,
            SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }


    public static void AttachToTaskbar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (handle == IntPtr.Zero || taskbar == IntPtr.Zero)
        {
            return;
        }

        SetWindowLongPtr(handle, GWLP_HWNDPARENT, taskbar);
        HideFromAltTab(window);
    }
    public static void HideFromAltTab(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var styles = GetWindowLongPtr(handle, GWL_EXSTYLE).ToInt64();
        styles |= WS_EX_TOOLWINDOW;
        styles &= ~WS_EX_APPWINDOW;
        SetWindowLongPtr(handle, GWL_EXSTYLE, new IntPtr(styles));
    }

    public static bool TryGetCursorPosition(out int x, out int y)
    {
        if (GetCursorPos(out var point))
        {
            x = point.X;
            y = point.Y;
            return true;
        }

        x = 0;
        y = 0;
        return false;
    }

    public static bool TryGetWindowRect(Window window, out NativeRect rect)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero && GetWindowRect(handle, out rect))
        {
            return true;
        }

        rect = default;
        return false;
    }

    public static void MoveWindow(Window window, int x, int y)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(
            handle,
            HWND_TOPMOST,
            x,
            y,
            0,
            0,
            SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    public static void SaveCurrentOffsets(Window window, AppSettings settings)
    {
        if (!TryGetWindowRect(window, out var bounds) ||
            !TryGetTaskbarRect(out var taskbar, out _))
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        var scale = GetScale(handle);
        settings.LeftOffset = Math.Max(0, (bounds.Left - taskbar.Left) / scale);
        settings.TopOffset = Math.Max(0, (bounds.Top - taskbar.Top) / scale);
        SettingsService.Save(settings);
    }

    private static double GetScale(IntPtr handle)
    {
        var dpi = handle == IntPtr.Zero ? 96u : GetDpiForWindow(handle);
        return dpi <= 0 ? 1.0 : dpi / 96.0;
    }
}

