using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace InternetSpeedMeter
{
    /// <summary>
    /// Development helper: renders the tray icon at every DPI size against taskbar-coloured
    /// backgrounds so the readout can be checked without squinting at the real tray.
    /// Run with: InternetSpeedMeter.exe --preview-icon out.png
    /// </summary>
    internal static class IconPreview
    {
        private static readonly string[][] Samples =
        {
            new[] { "0", "0" },
            new[] { "9.4K", "512" },
            new[] { "1.2M", "88K" },
            new[] { "118M", "9.9M" },
            new[] { "999", "999" }
        };

        public static void Dump(string path)
        {
            int[] sizes = { 16, 20, 24, 32 };
            int zoom = 4;
            int cell = 32 * zoom + 16;
            int width = (Samples.Length * cell) + 40;

            // Rows: each theme x each size, for the two-line layout and then the single-line one.
            int height = (sizes.Length * 4 * cell) + 40;

            using (Bitmap sheet = new Bitmap(width, height))
            using (Graphics g = Graphics.FromImage(sheet))
            using (IconRenderer renderer = new IconRenderer())
            {
                g.Clear(Color.FromArgb(0x80, 0x80, 0x88));
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;

                int row = 0;
                foreach (bool single in new[] { false, true })
                {
                foreach (bool dark in new[] { true, false })
                {
                    Palette palette = Palette.For(dark ? ThemeMode.Dark : ThemeMode.Light);
                    Color background = dark ? Color.FromArgb(0x20, 0x20, 0x20) : Color.FromArgb(0xF3, 0xF3, 0xF3);

                    foreach (int size in sizes)
                    {
                        for (int col = 0; col < Samples.Length; col++)
                        {
                            int x = 20 + (col * cell);
                            int y = 20 + (row * cell);

                            using (SolidBrush back = new SolidBrush(background))
                            {
                                g.FillRectangle(back, x, y, 32 * zoom, 32 * zoom);
                            }

                            Icon icon = single
                                ? renderer.RenderSingle(Samples[col][0], palette.Download, size)
                                : renderer.Render(
                                    Samples[col][0], Samples[col][1], palette.Download, palette.Upload, size);
                            if (icon == null)
                            {
                                continue;
                            }

                            using (icon)
                            using (Bitmap bitmap = icon.ToBitmap())
                            {
                                int drawn = size * zoom;
                                int offset = ((32 * zoom) - drawn) / 2;
                                g.DrawImage(bitmap, x + offset, y + offset, drawn, drawn);
                            }
                        }

                        row++;
                    }
                }
                }

                sheet.Save(path, ImageFormat.Png);
            }

            Console.WriteLine("wrote " + path);
        }

        /// <summary>
        /// Renders the detail flyout in both themes with synthetic traffic.
        /// Run with: InternetSpeedMeter.exe --preview-flyout out.png
        /// </summary>
        public static void DumpFlyout(string path)
        {
            Random random = new Random(7);
            double[] down = new double[60];
            double[] up = new double[60];
            for (int i = 0; i < down.Length; i++)
            {
                double wave = 0.5 + (0.5 * Math.Sin(i / 6.0));
                down[i] = wave * (6 * 1024 * 1024) * (0.6 + random.NextDouble() * 0.4);
                up[i] = wave * (700 * 1024) * (0.3 + random.NextDouble() * 0.7);
            }

            ThemeMode[] modes = { ThemeMode.Dark, ThemeMode.Light };
            Bitmap[] shots = new Bitmap[modes.Length];

            for (int i = 0; i < modes.Length; i++)
            {
                using (FlyoutForm form = new FlyoutForm())
                {
                    form.SetPalette(Palette.For(modes[i]));
                    form.UpdateData(
                        "Wi-Fi",
                        down[down.Length - 1],
                        up[up.Length - 1],
                        4.7 * 1024 * 1024 * 1024,
                        612.0 * 1024 * 1024,
                        DateTime.Now.AddMinutes(-73),
                        UnitMode.Bytes,
                        down,
                        up);

                    Bitmap shot = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(shot, new Rectangle(0, 0, form.Width, form.Height));
                    shots[i] = shot;
                }
            }

            int gap = 16;
            using (Bitmap sheet = new Bitmap((shots[0].Width * shots.Length) + (gap * 3), shots[0].Height + (gap * 2)))
            using (Graphics g = Graphics.FromImage(sheet))
            {
                g.Clear(Color.FromArgb(0x80, 0x80, 0x88));
                for (int i = 0; i < shots.Length; i++)
                {
                    g.DrawImage(shots[i], gap + (i * (shots[i].Width + gap)), gap);
                    shots[i].Dispose();
                }

                sheet.Save(path, ImageFormat.Png);
            }

            Console.WriteLine("wrote " + path);
        }
    }
}
