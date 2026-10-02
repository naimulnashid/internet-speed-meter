using System;
using System.Runtime.InteropServices;

namespace InternetSpeedMeter
{
    internal static class Native
    {
        public const int SM_CXSMICON = 49;

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr handle);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int index);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public int Width { get { return Right - Left; } }

            public int Height { get { return Bottom - Top; } }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;

            public POINT(int x, int y)
            {
                X = x;
                Y = y;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE
        {
            public int Width;
            public int Height;

            public SIZE(int width, int height)
            {
                Width = width;
                Height = height;
            }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_TOPMOST = 0x00000008;
        public const int WS_EX_TRANSPARENT = 0x00000020;

        public const int WM_APP = 0x8000;

        public const int ULW_ALPHA = 0x02;
        public const byte AC_SRC_OVER = 0x00;
        public const byte AC_SRC_ALPHA = 0x01;

        public const int GWLP_HWNDPARENT = -8;
        public const int GWL_EXSTYLE = -20;

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string windowName);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr window, out RECT rect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// Brings a window to the front and activates it.
        ///
        /// WS_EX_NOACTIVATE stops a window being activated by a click on it; it does not stop it
        /// being activated when asked outright, which is what this is for.
        /// </summary>
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("dwmapi.dll", EntryPoint = "DwmFlush")]
        private static extern int DwmFlushNative();

        /// <summary>
        /// Blocks until the compositor has finished its next frame, or fails if there is nothing
        /// composing (composition off, a remote session, DWM restarting) rather than waiting.
        /// </summary>
        public static bool DwmFlush()
        {
            try
            {
                return DwmFlushNative() == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr window, int index, int value);

        /// <summary>
        /// The window that owns this one, or <see cref="IntPtr.Zero"/>. Thirty-two bit Windows
        /// exports no ...Ptr entry point at all -- there it is a macro over the long version.
        /// </summary>
        public static IntPtr GetWindowOwner(IntPtr window)
        {
            return IntPtr.Size == 8
                ? GetWindowLongPtr64(window, GWLP_HWNDPARENT)
                : new IntPtr(GetWindowLong32(window, GWLP_HWNDPARENT));
        }

        /// <summary>Hands <paramref name="owner"/> ownership of <paramref name="window"/>.</summary>
        public static void SetWindowOwner(IntPtr window, IntPtr owner)
        {
            if (IntPtr.Size == 8)
            {
                SetWindowLongPtr64(window, GWLP_HWNDPARENT, owner);
            }
            else
            {
                SetWindowLong32(window, GWLP_HWNDPARENT, owner.ToInt32());
            }
        }

        /// <summary>The extended style bits of a window.</summary>
        public static int GetWindowExStyle(IntPtr window)
        {
            return IntPtr.Size == 8
                ? GetWindowLongPtr64(window, GWL_EXSTYLE).ToInt32()
                : GetWindowLong32(window, GWL_EXSTYLE);
        }

        /// <summary>Replaces the extended style bits of a window.</summary>
        public static void SetWindowExStyle(IntPtr window, int style)
        {
            if (IntPtr.Size == 8)
            {
                SetWindowLongPtr64(window, GWL_EXSTYLE, new IntPtr(style));
            }
            else
            {
                SetWindowLong32(window, GWL_EXSTYLE, style);
            }
        }

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr window, IntPtr dc);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UpdateLayeredWindow(
            IntPtr window,
            IntPtr destinationDc,
            ref POINT destinationPoint,
            ref SIZE size,
            IntPtr sourceDc,
            ref POINT sourcePoint,
            int colorKey,
            ref BLENDFUNCTION blend,
            int flags);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteObject(IntPtr obj);

        private const int ATTACH_PARENT_PROCESS = -1;

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachConsole(int processId);

        /// <summary>Lets the GUI executable print into the console that launched it, if any.</summary>
        public static bool AttachParentConsole()
        {
            try
            {
                return AttachConsole(ATTACH_PARENT_PROCESS);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        /// <summary>Rounds a borderless window on Windows 11; older builds ignore the attribute.</summary>
        public static void RoundCorners(IntPtr window)
        {
            try
            {
                int preference = DWMWCP_ROUND;
                DwmSetWindowAttribute(window, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
            }
            catch (Exception)
            {
                // dwmapi is present on every supported Windows, but never crash over cosmetics.
            }
        }

        /// <summary>Tray icon edge in physical pixels for the current DPI (16, 20, 24, 32...).</summary>
        public static int TrayIconSize()
        {
            int size = 0;
            try
            {
                size = GetSystemMetrics(SM_CXSMICON);
            }
            catch (Exception)
            {
                size = 0;
            }

            if (size < 16)
            {
                size = 16;
            }

            if (size > 64)
            {
                size = 64;
            }

            return size;
        }
    }
}
