using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace InternetSpeedMeter
{
    /// <summary>
    /// Borderless detail panel shown on left click: live rates, a 60 second graph and session
    /// totals. Everything is painted by hand so it stays crisp at any DPI.
    /// </summary>
    internal sealed class FlyoutForm : Form
    {
        private readonly float _scale;
        private Palette _palette = Palette.For(ThemeMode.Auto);

        private Font _fontLabel;
        private Font _fontValue;
        private Font _fontUnit;
        private Font _fontTiny;

        private string _adapter = "";
        private double _down;
        private double _up;
        private double _totalDown;
        private double _totalUp;
        private DateTime _since = DateTime.Now;
        private UnitMode _units = UnitMode.Bytes;
        private double[] _historyDown = new double[0];
        private double[] _historyUp = new double[0];
        private double _peak;

        public FlyoutForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            KeyPreview = true;

            using (Graphics g = CreateGraphics())
            {
                _scale = g.DpiX / 96f;
            }

            ClientSize = new Size(S(292), S(214));
            BuildFonts();
        }

        private int S(double value)
        {
            return (int)Math.Round(value * _scale);
        }

        private void BuildFonts()
        {
            DisposeFonts();
            _fontLabel = new Font("Segoe UI", S(12), FontStyle.Regular, GraphicsUnit.Pixel);
            _fontValue = new Font("Segoe UI Semibold", S(22), FontStyle.Regular, GraphicsUnit.Pixel);
            _fontUnit = new Font("Segoe UI", S(12), FontStyle.Regular, GraphicsUnit.Pixel);
            _fontTiny = new Font("Segoe UI", S(11), FontStyle.Regular, GraphicsUnit.Pixel);
        }

        private void DisposeFonts()
        {
            if (_fontLabel != null)
            {
                _fontLabel.Dispose();
                _fontValue.Dispose();
                _fontUnit.Dispose();
                _fontTiny.Dispose();
            }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                return cp;
            }
        }

        public void SetPalette(Palette palette)
        {
            _palette = palette;
            BackColor = palette.CardBack;
            if (Visible)
            {
                Invalidate();
            }
        }

        public void UpdateData(
            string adapter,
            double downBytesPerSecond,
            double upBytesPerSecond,
            double totalDown,
            double totalUp,
            DateTime since,
            UnitMode units,
            double[] historyDown,
            double[] historyUp)
        {
            _adapter = adapter;
            _down = downBytesPerSecond;
            _up = upBytesPerSecond;
            _totalDown = totalDown;
            _totalUp = totalUp;
            _since = since;
            _units = units;
            _historyDown = historyDown;
            _historyUp = historyUp;

            if (Visible)
            {
                Invalidate();
            }
        }

        /// <summary>
        /// Places the panel beside the click and clear of the taskbar.
        ///
        /// The working area is not enough on its own: an auto-hiding taskbar reserves none of it,
        /// so a panel placed at the bottom of the work area lands directly on top of the bar and
        /// covers its icons. The visible taskbar rectangle is therefore subtracted explicitly.
        /// </summary>
        public void ShowNear(Point anchor)
        {
            Rectangle limit = Screen.FromPoint(anchor).WorkingArea;

            Rectangle bar;
            if (TaskbarWidget.TryGetVisibleTaskbarRect(out bar))
            {
                if (bar.Width > bar.Height)
                {
                    // Horizontal bar: give up the band it occupies, top or bottom.
                    if (bar.Top <= limit.Top + (bar.Height / 2))
                    {
                        limit = Rectangle.FromLTRB(limit.Left, Math.Max(limit.Top, bar.Bottom), limit.Right, limit.Bottom);
                    }
                    else
                    {
                        limit = Rectangle.FromLTRB(limit.Left, limit.Top, limit.Right, Math.Min(limit.Bottom, bar.Top));
                    }
                }
                else if (bar.Left <= limit.Left + (bar.Width / 2))
                {
                    limit = Rectangle.FromLTRB(Math.Max(limit.Left, bar.Right), limit.Top, limit.Right, limit.Bottom);
                }
                else
                {
                    limit = Rectangle.FromLTRB(limit.Left, limit.Top, Math.Min(limit.Right, bar.Left), limit.Bottom);
                }
            }

            int margin = S(8);

            int x = anchor.X - (Width / 2);
            x = Math.Max(limit.Left + margin, Math.Min(x, limit.Right - Width - margin));

            int y = anchor.Y > limit.Top + (limit.Height / 2)
                ? limit.Bottom - Height - margin
                : limit.Top + margin;

            // A limit smaller than the panel (tiny screen, huge bar) must not push it off-screen.
            y = Math.Max(limit.Top, y);

            Location = new Point(x, y);
            Invalidate();
            Show();
            Activate();
        }

        /// <summary>
        /// When the panel is open and the tray icon is clicked, the click first deactivates the
        /// panel (hiding it) and only then arrives as a tray click. The caller uses this stamp to
        /// tell that apart from a genuine "open me" click.
        /// </summary>
        public DateTime HiddenAt { get; private set; }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible)
            {
                HiddenAt = DateTime.UtcNow;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.RoundCorners(Handle);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            Hide();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape)
            {
                Hide();
            }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button == MouseButtons.Left)
            {
                Hide();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(_palette.CardBack);

            using (Pen border = new Pen(_palette.CardBorder))
            {
                g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            }

            int pad = S(14);
            int right = Width - pad;

            // Header: which adapter is being metered, and how long the session has run.
            using (SolidBrush dim = new SolidBrush(_palette.TextDim))
            {
                g.DrawString(Fmt.Ellipsize(_adapter, 26), _fontLabel, dim, pad, S(10));

                string uptime = Fmt.Duration(DateTime.Now - _since);
                SizeF size = g.MeasureString(uptime, _fontTiny);
                g.DrawString(uptime, _fontTiny, dim, right - size.Width, S(11));
            }

            int rowY = S(34);
            DrawRate(g, pad, rowY, true, _down);
            DrawRate(g, pad, rowY + S(38), false, _up);

            Rectangle graph = new Rectangle(pad, S(112), Width - (pad * 2), S(58));
            DrawGraph(g, graph);

            using (SolidBrush dim = new SolidBrush(_palette.TextDim))
            {
                string totals = "Session   ▼ " + Fmt.Total(_totalDown) + "    ▲ " + Fmt.Total(_totalUp);
                g.DrawString(totals, _fontTiny, dim, pad, Height - S(24));

                string peak = "peak " + Fmt.SpeedLong(_peak, _units);
                SizeF size = g.MeasureString(peak, _fontTiny);
                g.DrawString(peak, _fontTiny, dim, right - size.Width, Height - S(24));
            }
        }

        private void DrawRate(Graphics g, int x, int y, bool download, double bytesPerSecond)
        {
            Color color = download ? _palette.Download : _palette.Upload;
            int arrow = S(9);

            using (SolidBrush brush = new SolidBrush(color))
            {
                PointF[] triangle = download
                    ? new PointF[]
                    {
                        new PointF(x, y + S(9)),
                        new PointF(x + arrow, y + S(9)),
                        new PointF(x + (arrow / 2f), y + S(9) + arrow)
                    }
                    : new PointF[]
                    {
                        new PointF(x, y + S(9) + arrow),
                        new PointF(x + arrow, y + S(9) + arrow),
                        new PointF(x + (arrow / 2f), y + S(9))
                    };
                g.FillPolygon(brush, triangle);
            }

            string text = Fmt.SpeedLong(bytesPerSecond, _units);
            int split = text.IndexOf(' ');
            string value = split > 0 ? text.Substring(0, split) : text;
            string unit = split > 0 ? text.Substring(split + 1) : "";

            int textX = x + arrow + S(8);
            using (SolidBrush brush = new SolidBrush(_palette.Text))
            {
                g.DrawString(value, _fontValue, brush, textX, y);
            }

            SizeF valueSize = g.MeasureString(value, _fontValue);
            using (SolidBrush dim = new SolidBrush(_palette.TextDim))
            {
                g.DrawString(unit, _fontUnit, dim, textX + valueSize.Width - S(4), y + S(13));
            }
        }

        private void DrawGraph(Graphics g, Rectangle area)
        {
            using (SolidBrush grid = new SolidBrush(_palette.GraphGrid))
            {
                g.FillRectangle(grid, area);
            }

            double peak = 0.0;
            for (int i = 0; i < _historyDown.Length; i++)
            {
                if (_historyDown[i] > peak)
                {
                    peak = _historyDown[i];
                }
            }

            for (int i = 0; i < _historyUp.Length; i++)
            {
                if (_historyUp[i] > peak)
                {
                    peak = _historyUp[i];
                }
            }

            _peak = peak;

            // A floor keeps an idle graph flat instead of amplifying a few stray bytes.
            double scale = Math.Max(peak, 8 * 1024.0);

            // Download first, upload on top: upload is usually the smaller series and would
            // otherwise disappear under the download fill.
            DrawSeries(g, area, _historyDown, scale, _palette.Download);
            DrawSeries(g, area, _historyUp, scale, _palette.Upload);
        }

        private void DrawSeries(Graphics g, Rectangle area, double[] values, double scale, Color color)
        {
            if (values == null || values.Length < 2)
            {
                return;
            }

            // Newest sample sits at the right edge; a short history starts partway across.
            float step = area.Width / (float)(values.Length - 1);
            PointF[] line = new PointF[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                double ratio = values[i] / scale;
                if (ratio > 1.0)
                {
                    ratio = 1.0;
                }

                line[i] = new PointF(
                    area.Left + (i * step),
                    area.Bottom - (float)(ratio * (area.Height - 2)) - 1);
            }

            PointF[] fill = new PointF[line.Length + 2];
            Array.Copy(line, fill, line.Length);
            fill[line.Length] = new PointF(area.Right, area.Bottom);
            fill[line.Length + 1] = new PointF(area.Left, area.Bottom);

            using (SolidBrush brush = new SolidBrush(Color.FromArgb(70, color)))
            {
                g.FillPolygon(brush, fill);
            }

            using (Pen pen = new Pen(color, Math.Max(1f, _scale)))
            {
                pen.LineJoin = LineJoin.Round;
                g.DrawLines(pen, line);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeFonts();
            }

            base.Dispose(disposing);
        }
    }
}
