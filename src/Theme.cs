using System;
using System.Drawing;
using Microsoft.Win32;

namespace InternetSpeedMeter
{
    /// <summary>Colours for the tray icon and the flyout, following the Windows taskbar theme.</summary>
    internal sealed class Palette
    {
        public bool Dark;

        public Color Download;
        public Color Upload;

        public Color CardBack;
        public Color CardBorder;
        public Color Text;
        public Color TextDim;
        public Color GraphGrid;

        public static Palette For(ThemeMode mode)
        {
            bool dark;
            switch (mode)
            {
                case ThemeMode.Light:
                    // A light *taskbar* wants dark icon text.
                    dark = false;
                    break;
                case ThemeMode.Dark:
                    dark = true;
                    break;
                default:
                    dark = !SystemUsesLightTaskbar();
                    break;
            }

            Palette p = new Palette();
            p.Dark = dark;

            if (dark)
            {
                p.Download = Color.FromArgb(0x6E, 0xE7, 0xA8);
                p.Upload = Color.FromArgb(0xFF, 0xC4, 0x6B);
                p.CardBack = Color.FromArgb(0x1F, 0x1F, 0x23);
                p.CardBorder = Color.FromArgb(0x3A, 0x3A, 0x42);
                p.Text = Color.FromArgb(0xF2, 0xF2, 0xF5);
                p.TextDim = Color.FromArgb(0x9A, 0x9A, 0xA6);
                p.GraphGrid = Color.FromArgb(0x2E, 0x2E, 0x36);
            }
            else
            {
                p.Download = Color.FromArgb(0x0B, 0x6B, 0x35);
                p.Upload = Color.FromArgb(0x9A, 0x54, 0x00);
                p.CardBack = Color.FromArgb(0xFA, 0xFA, 0xFC);
                p.CardBorder = Color.FromArgb(0xD8, 0xD8, 0xE0);
                p.Text = Color.FromArgb(0x1A, 0x1A, 0x1F);
                p.TextDim = Color.FromArgb(0x66, 0x66, 0x70);
                p.GraphGrid = Color.FromArgb(0xE6, 0xE6, 0xEC);
            }

            return p;
        }

        /// <summary>
        /// Windows exposes taskbar/tray theming separately from app theming; the tray icon has to
        /// follow SystemUsesLightTheme. Missing value means the classic dark taskbar.
        /// </summary>
        public static bool SystemUsesLightTaskbar()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key == null)
                    {
                        return false;
                    }

                    object value = key.GetValue("SystemUsesLightTheme");
                    if (value is int)
                    {
                        return ((int)value) != 0;
                    }
                }
            }
            catch (Exception)
            {
                // Locked-down registry: assume the default dark taskbar.
            }

            return false;
        }
    }
}
