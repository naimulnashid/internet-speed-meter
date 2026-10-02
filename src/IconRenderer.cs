using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace InternetSpeedMeter
{
    /// <summary>
    /// Draws the two-line speed readout that becomes the tray icon. Rendering is cheap but it runs
    /// once a second forever, so results are cached by content and every HICON is destroyed.
    /// </summary>
    internal sealed class IconRenderer : IDisposable
    {
        private readonly Dictionary<int, Font> _fonts = new Dictionary<int, Font>();
        private readonly Dictionary<int, Ink> _ink = new Dictionary<int, Ink>();

        // Two lines can stretch moderately; a single line is an explicit "make it big" choice.
        private const float TwoLineStretch = 1.8f;
        private const float SingleLineStretch = 2.6f;
        private readonly StringFormat _format;
        private string _lastKey;
        private IntPtr _currentHandle = IntPtr.Zero;
        private IntPtr _retiredHandle = IntPtr.Zero;
        private string _fontFamily;

        public IconRenderer()
        {
            // Rows are positioned by hand, so keep the origin at the top-left of each glyph.
            _format = new StringFormat(StringFormat.GenericTypographic);
            _format.Alignment = StringAlignment.Near;
            _format.LineAlignment = StringAlignment.Near;
            _format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
            _fontFamily = PickFamily();
        }

        // Tahoma keeps its shape at 7-8 px far better than Segoe UI, which is what the tray needs
        // at 100% scaling. Fall back only if it is somehow unavailable.
        private static string PickFamily()
        {
            string[] preferred = { "Tahoma", "Segoe UI", "Microsoft Sans Serif" };
            foreach (string name in preferred)
            {
                try
                {
                    using (FontFamily family = new FontFamily(name))
                    {
                        return family.Name;
                    }
                }
                catch (ArgumentException)
                {
                }
            }

            return FontFamily.GenericSansSerif.Name;
        }

        /// <summary>
        /// Returns a new icon, or null when the content is unchanged and the caller should keep the
        /// icon it already has.
        /// </summary>
        public Icon Render(string top, string bottom, Color topColor, Color bottomColor, int size)
        {
            string key = "2|" + size + "|" + top + "|" + bottom + "|" + topColor.ToArgb() + "|" + bottomColor.ToArgb();
            return Build(key, size, delegate(Graphics g)
            {
                float rowHeight = size / 2f;
                DrawRow(g, top, topColor, new RectangleF(0, 0, size, rowHeight), size, TwoLineStretch);
                DrawRow(g, bottom, bottomColor, new RectangleF(0, rowHeight, size, rowHeight), size, TwoLineStretch);
            });
        }

        /// <summary>
        /// One metric across the whole icon. Twice the row height means far larger glyphs, which is
        /// the only real answer when two lines are too small to read at a glance.
        /// </summary>
        public Icon RenderSingle(string text, Color color, int size)
        {
            string key = "1|" + size + "|" + text + "|" + color.ToArgb();
            return Build(key, size, delegate(Graphics g)
            {
                DrawRow(g, text, color, new RectangleF(0, 0, size, size), size, SingleLineStretch);
            });
        }

        private Icon Build(string key, int size, Action<Graphics> draw)
        {
            if (key == _lastKey)
            {
                return null;
            }

            _lastKey = key;

            using (Bitmap bitmap = new Bitmap(size, size))
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;

                    // Sub-pixel smoothing has no meaning on a transparent bitmap, and at 16 px the
                    // hinted bi-level rendering is simply easier to read.
                    g.TextRenderingHint = size <= 17
                        ? TextRenderingHint.SingleBitPerPixelGridFit
                        : TextRenderingHint.AntiAliasGridFit;

                    draw(g);
                }

                IntPtr handle = bitmap.GetHicon();

                // The handle from two generations back is guaranteed to be off the NotifyIcon by
                // now, so this is the safe moment to release it.
                if (_retiredHandle != IntPtr.Zero)
                {
                    Native.DestroyIcon(_retiredHandle);
                }

                _retiredHandle = _currentHandle;
                _currentHandle = handle;
                return Icon.FromHandle(handle);
            }
        }

        /// <summary>
        /// Fits one row of text.
        ///
        /// The tray icon is square and tiny, so width is almost always the binding constraint: four
        /// characters across a 24 px icon leave 6 px each, and a proportional face at that width is
        /// only ~8 px tall, wasting a third of the row. So the font is picked by width, then the
        /// glyphs are stretched vertically to fill the row — the tall, condensed look every good
        /// tray meter ends up with. Digits and unit letters have no descenders, which is what makes
        /// the stretch safe.
        /// </summary>
        private void DrawRow(Graphics g, string text, Color color, RectangleF bounds, int iconSize, float maxStretch)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            // Leave a hairline between the two rows so they never read as one block; a single line
            // gets a slightly larger breathing margin against the icon edges instead.
            float gap = bounds.Height > iconSize * 0.75f
                ? Math.Max(2f, iconSize / 8f)
                : (iconSize >= 20 ? 2f : 1f);
            float targetInk = bounds.Height - gap;

            // Small icons get the bitmap font; anything larger has room for real type.
            if (iconSize < 20)
            {
                bool tall = targetInk >= PixelFont.TallHeight;
                int pixelWidth = PixelFont.Measure(text, tall);
                if (pixelWidth > 0 && pixelWidth <= bounds.Width)
                {
                    int glyphHeight = tall ? PixelFont.TallHeight : PixelFont.ShortHeight;

                    // Whole-pixel vertical scaling only: half a source row would blur the edges
                    // that make this font readable in the first place.
                    int scaleY = Math.Max(1, (int)Math.Floor(targetInk / glyphHeight));
                    int drawn = glyphHeight * scaleY;

                    int px = (int)Math.Round(bounds.X + ((bounds.Width - pixelWidth) / 2f));
                    int py = (int)Math.Round(bounds.Y + ((bounds.Height - drawn) / 2f));
                    PixelFont.Draw(g, text, px, py, color, tall, scaleY);
                    return;
                }
            }

            int max = (int)Math.Round(bounds.Height * 1.6f);
            int min = Math.Max(5, iconSize / 4);

            for (int px = max; px >= min; px--)
            {
                Font font = GetFont(px);
                Ink ink = MeasureInk(g, px);

                // Never start from a size that would already overflow the row before stretching.
                if (ink.Height > targetInk && px > min)
                {
                    continue;
                }

                float[] widths = new float[text.Length];
                float natural = 0f;
                for (int i = 0; i < text.Length; i++)
                {
                    widths[i] = g.MeasureString(text[i].ToString(), font, PointF.Empty, _format).Width;
                    natural += widths[i];
                }

                float tighten = 0f;
                int gaps = text.Length - 1;
                if (natural > bounds.Width && gaps > 0)
                {
                    tighten = Math.Min(1f, (natural - bounds.Width) / gaps);
                }

                float finalWidth = natural - (tighten * gaps);
                if (finalWidth > bounds.Width + 0.6f && px > min)
                {
                    continue;
                }

                DrawStretched(g, text, font, color, bounds, widths, tighten, finalWidth, ink, targetInk, maxStretch);
                return;
            }
        }

        private void DrawStretched(
            Graphics g,
            string text,
            Font font,
            Color color,
            RectangleF bounds,
            float[] widths,
            float tighten,
            float finalWidth,
            Ink ink,
            float targetInk,
            float maxStretch)
        {
            // Cap the distortion: stretched too far, the digits stop reading as digits.
            float scaleY = ink.Height > 0.5f ? targetInk / ink.Height : 1f;
            scaleY = Math.Max(0.9f, Math.Min(maxStretch, scaleY));

            float rowCenter = bounds.Y + (bounds.Height / 2f);
            float x = bounds.X + ((bounds.Width - finalWidth) / 2f);

            // Scale about the row centre, then place the text so its ink lands on that centre.
            GraphicsState state = g.Save();
            g.TranslateTransform(0f, rowCenter);
            g.ScaleTransform(1f, scaleY);
            float y = -(ink.Top + (ink.Height / 2f));

            using (SolidBrush brush = new SolidBrush(color))
            {
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];

                    // A typographic full stop at these sizes anti-aliases down to almost nothing,
                    // and "1.2M" misread as "12M" is a tenfold error. Draw it as a solid block.
                    if (c == '.' || c == ',')
                    {
                        float dot = Math.Max(1f, font.Size / 7f);

                        // Text draws at local y, so its ink ends at y + ink.Top + ink.Height;
                        // sit the block on that baseline.
                        float dotY = y + ink.Top + ink.Height - dot;
                        g.FillRectangle(brush, (float)Math.Round(x), dotY, dot, dot);
                    }
                    else
                    {
                        g.DrawString(c.ToString(), font, brush, (float)Math.Round(x), y, _format);
                    }

                    x += widths[i] - tighten;
                }
            }

            g.Restore(state);
        }

        /// <summary>Where the ink of the digit/unit glyph set actually sits inside a line box.</summary>
        private struct Ink
        {
            public float Top;
            public float Height;
        }

        // GDI+ line boxes include ascent, descent and leading, none of which our glyphs use: digits
        // and K/M/G/T/B have no descenders and no accents. Measuring the real ink once per font size
        // is what lets the row be filled instead of padded.
        private Ink MeasureInk(Graphics g, int pixelHeight)
        {
            Ink ink;
            if (_ink.TryGetValue(pixelHeight, out ink))
            {
                return ink;
            }

            const string Sample = "0123456789.KMGTBb";
            Font font = GetFont(pixelHeight);
            SizeF box = g.MeasureString(Sample, font, PointF.Empty, _format);

            int width = (int)Math.Ceiling(box.Width) + 4;
            int height = (int)Math.Ceiling(box.Height) + 8;

            ink.Top = 0f;
            ink.Height = box.Height;

            using (Bitmap probe = new Bitmap(Math.Max(width, 8), Math.Max(height, 8)))
            {
                using (Graphics pg = Graphics.FromImage(probe))
                {
                    pg.Clear(Color.Transparent);
                    pg.TextRenderingHint = g.TextRenderingHint;
                    pg.DrawString(Sample, font, Brushes.White, 2f, 2f, _format);
                }

                int first = -1;
                int last = -1;
                for (int y = 0; y < probe.Height; y++)
                {
                    bool lit = false;
                    for (int x = 0; x < probe.Width; x++)
                    {
                        if (probe.GetPixel(x, y).A > 0)
                        {
                            lit = true;
                            break;
                        }
                    }

                    if (lit)
                    {
                        if (first < 0)
                        {
                            first = y;
                        }

                        last = y;
                    }
                }

                if (first >= 0)
                {
                    ink.Top = first - 2f;
                    ink.Height = (last - first) + 1f;
                }
            }

            _ink[pixelHeight] = ink;
            return ink;
        }

        private Font GetFont(int pixelHeight)
        {
            Font font;
            if (_fonts.TryGetValue(pixelHeight, out font))
            {
                return font;
            }

            font = new Font(_fontFamily, pixelHeight, FontStyle.Regular, GraphicsUnit.Pixel);
            _fonts[pixelHeight] = font;
            return font;
        }

        public void Dispose()
        {
            foreach (KeyValuePair<int, Font> pair in _fonts)
            {
                pair.Value.Dispose();
            }

            _fonts.Clear();
            _format.Dispose();

            if (_retiredHandle != IntPtr.Zero)
            {
                Native.DestroyIcon(_retiredHandle);
                _retiredHandle = IntPtr.Zero;
            }

            if (_currentHandle != IntPtr.Zero)
            {
                Native.DestroyIcon(_currentHandle);
                _currentHandle = IntPtr.Zero;
            }
        }
    }
}
