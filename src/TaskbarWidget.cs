using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Threading;
using System.Windows.Forms;

namespace InternetSpeedMeter
{
    internal enum WidgetPlacement
    {
        /// <summary>Drawn on the taskbar right now.</summary>
        Shown,

        /// <summary>The taskbar exists but is auto-hidden at the moment.</summary>
        TemporarilyHidden,

        /// <summary>No horizontal taskbar, or no room on it: the tray icon must carry the numbers.</summary>
        Unavailable
    }

    /// <summary>
    /// The readout as taskbar text, in the clock's font and size.
    ///
    /// A tray icon is a square roughly 24 px across, so four characters get about six pixels each:
    /// clock-sized text cannot fit there no matter how it is drawn. The clock is not an icon — it is
    /// text with room around it. So this is a layered, topmost, non-activating window parked in the
    /// empty taskbar space just left of the notification area, painted with per-pixel alpha so the
    /// taskbar's own background (acrylic, accent colour, whatever) shows through untouched. The
    /// taskbar owns it, which is what keeps it above the bar even when Start lifts the bar into a
    /// z-order band this process cannot reach: see <see cref="EnsureOwnedByTaskbar"/>.
    /// </summary>
    internal sealed class TaskbarWidget : Form
    {
        private const int SidePadding = 10;
        private const int VerticalPadding = 4;
        private const int GapFromTray = 8;

        /// <summary>Posted by the watcher thread when the taskbar's geometry has changed.</summary>
        private const int WmFollow = Native.WM_APP + 1;

        /// <summary>Cadence the watcher falls back to when there is no compositor to pace it.</summary>
        private const int UncomposedFrameMs = 8;

        /// <summary>
        /// How long after the last movement the watcher keeps sampling every frame. Longer than the
        /// ~185 ms slide, so an animation is never throttled part way through.
        /// </summary>
        private const int AnimationTailMs = 400;

        /// <summary>
        /// Sampling interval once the taskbar has gone quiet. Sleep rounds this up to the system
        /// tick, about 15 ms, which is the cost of one wakeup per 60 Hz frame instead of one per
        /// frame on a screen that may be running at four times that.
        /// </summary>
        private const int IdleIntervalMs = 8;

        private Palette _palette = Palette.For(ThemeMode.Auto);
        private UnitMode _units = UnitMode.Bytes;
        private TaskbarSide _side = TaskbarSide.Left;
        private double _down;
        private double _up;
        private Font _font;
        private int _fontPixels;
        private int _measuredWidth;
        private bool _placed;
        private bool _dirty = true;
        private int _lastX;
        private int _lastY;
        private int _lastWidth;
        private int _lastHeight;
        private bool _clickThrough;
        private IntPtr _clickThroughHandle;
        private IntPtr _trayHandle;
        private IntPtr _notifyHandle;
        private IntPtr _ownedHandle;
        private IntPtr _ownedTo;
        private Thread _watcher;
        private volatile bool _watching;
        private volatile IntPtr _followTarget;
        private int _followPending;

        public TaskbarWidget()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Visible = false;
        }

        public event EventHandler LeftClicked;

        public event EventHandler RightClicked;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW |
                              Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        /// <summary>Never take focus from whatever the user is working in.</summary>
        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        public void SetPalette(Palette palette)
        {
            _palette = palette;
        }

        public void SetUnits(UnitMode units)
        {
            _units = units;
            _measuredWidth = 0;
        }

        public void SetSide(TaskbarSide side)
        {
            _side = side;
        }

        /// <summary>
        /// Takes the foreground on the press, so that the menu the release opens is a foreground
        /// window's menu.
        ///
        /// The readout is WS_EX_NOACTIVATE, and deliberately so -- a speed meter must never pull
        /// the caret out of whatever is being typed in -- but a menu that belongs to a background
        /// application is a menu nothing ever tells to close, so the foreground has to be asked for
        /// outright. Asking for it is not instant: the activation travels as messages, and the ones
        /// that reach this thread arrive only once it is back in its message loop. Ask on the
        /// release and they land on top of the menu that release just opened, which reads to a
        /// dropdown as the user having clicked elsewhere and shuts it again the moment it appears:
        /// the first right-click after the application lost the foreground opened nothing at all,
        /// and only the second -- with the foreground already ours and nothing left to change --
        /// stayed up. The press is one whole trip round the message loop earlier, which is all the
        /// activation needs to be over and done with before there is a menu for it to disturb.
        /// </summary>
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);

            if (e.Button == MouseButtons.Right)
            {
                Native.SetForegroundWindow(Handle);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            if (e.Button == MouseButtons.Left && LeftClicked != null)
            {
                LeftClicked(this, EventArgs.Empty);
            }
            else if (e.Button == MouseButtons.Right && RightClicked != null)
            {
                RightClicked(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// The taskbar rectangle, but only while it is really on screen. Other windows need this to
        /// keep clear of the bar: an auto-hiding taskbar reserves no working area, so
        /// <see cref="Screen.WorkingArea"/> alone will happily place a window straight over it.
        /// </summary>
        public static bool TryGetVisibleTaskbarRect(out Rectangle bar)
        {
            bar = Rectangle.Empty;

            IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero || !Native.IsWindowVisible(tray))
            {
                return false;
            }

            Native.RECT rect;
            if (!Native.GetWindowRect(tray, out rect))
            {
                return false;
            }

            bar = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);

            Rectangle screen = Screen.FromRectangle(bar).Bounds;
            Rectangle onScreen = Rectangle.Intersect(bar, screen);
            if (onScreen.IsEmpty)
            {
                return false;
            }

            // Parked auto-hidden bars keep a couple of pixels on screen: not something to dodge.
            bool horizontal = bar.Width > bar.Height;
            int visible = horizontal ? onScreen.Height : onScreen.Width;
            int full = horizontal ? bar.Height : bar.Width;
            if (visible < full * 0.6)
            {
                return false;
            }

            bar = onScreen;
            return true;
        }

        /// <summary>New reading. The repaint itself happens in <see cref="Sync"/>.</summary>
        public WidgetPlacement Update(double downBytesPerSecond, double upBytesPerSecond)
        {
            _down = downBytesPerSecond;
            _up = upBytesPerSecond;
            _dirty = true;
            return Sync();
        }

        /// <summary>
        /// Rides the taskbar.
        ///
        /// An auto-hiding taskbar does not teleport: Windows animates its window rectangle off and
        /// on screen over roughly 185 ms with an ease-out. So the widget simply adopts that
        /// rectangle every frame and inherits the exact same motion — no easing curve of its own to
        /// keep in step, and no threshold at which it pops in or out. At rest the taskbar leaves a
        /// two pixel sliver on screen and the widget rides along, showing only transparent padding.
        ///
        /// Position-only changes move the window without repainting, so following the animation at
        /// frame rate costs a SetWindowPos per frame and nothing else.
        /// </summary>
        public WidgetPlacement Sync()
        {
            Native.RECT taskbar;
            Native.RECT anchor;
            int visibleHeight;
            if (!TryGetTaskbar(out taskbar, out anchor, out visibleHeight))
            {
                HideWidget();
                return WidgetPlacement.Unavailable;
            }

            int height = taskbar.Height;
            if (height < 20 || taskbar.Width < 200)
            {
                HideWidget();
                return WidgetPlacement.Unavailable;
            }

            EnsureOwnedByTaskbar();
            EnsureFont(height);

            int width = _measuredWidth;
            int y = taskbar.Top;
            int edge = Math.Max(8, height / 6);

            // Windows 11 centres the taskbar buttons by default, which leaves the left end empty
            // and makes it the calmer place to read from; the right end butts against the tray.
            int x = _side == TaskbarSide.Left
                ? taskbar.Left + edge
                : anchor.Left - width - GapFromTray;

            // Never encroach on the notification area, whichever end we are anchored to.
            if (x < taskbar.Left || x + width > anchor.Left - 8)
            {
                HideWidget();
                return WidgetPlacement.Unavailable;
            }

            bool resized = width != _lastWidth || height != _lastHeight;
            bool moved = x != _lastX || y != _lastY;

            // Repaint only once a meaningful sliver is on screen: no point drawing numbers into the
            // two pixel stub of a parked taskbar, but early enough in the slide that nothing pops.
            bool worthDrawing = visibleHeight >= height * 0.15;

            if (worthDrawing && (_dirty || !_placed || resized))
            {
                RenderFrame(width, height, x, y);
            }
            else if (_placed && moved)
            {
                // Moved without touching the z-order, which is where all the cost of this call is:
                // the taskbar owns this window, so re-ordering it means synchronising with
                // Explorer's thread, and during an animation that is the busiest thread on the
                // machine — 100 ms a call, measured, on every frame of a slide. Ownership is
                // what holds the z-order now, so a moving widget never has to ask for it.
                Native.SetWindowPos(
                    Handle,
                    IntPtr.Zero,
                    x,
                    y,
                    width,
                    height,
                    Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
            }

            _lastX = x;
            _lastY = y;
            _lastWidth = width;
            _lastHeight = height;
            if (worthDrawing)
            {
                _dirty = false;
            }

            WidgetPlacement placement = visibleHeight >= height * 0.6
                ? WidgetPlacement.Shown
                : WidgetPlacement.TemporarilyHidden;

            SetClickThrough(placement != WidgetPlacement.Shown);

            return placement;
        }

        /// <summary>
        /// Takes the readout out of the way of the mouse while the taskbar is away, and puts it
        /// back once the bar is out.
        ///
        /// An auto-hiding taskbar does not leave the screen: it parks with a two pixel sliver still
        /// on it, and that sliver is how Explorer notices the pointer arriving at the edge and
        /// slides the bar back out. The readout rides the bar down and covers its own stretch of
        /// that sliver -- and it is hit-testable there, because every pixel of it is painted with at
        /// least an alpha of 1 so that the gaps between the glyphs are clickable too. So the
        /// pointer reached the readout instead of the taskbar, silently, and the bar simply never
        /// came back at this end of the screen; every other end of it worked.
        ///
        /// WS_EX_TRANSPARENT takes a window out of hit-testing altogether, so the mouse falls
        /// straight through to the sliver underneath. It is only wanted while the bar is away: once
        /// the bar is out the readout is something the user can see and click, and the taskbar
        /// underneath it has nothing left to reveal.
        /// </summary>
        private void SetClickThrough(bool through)
        {
            IntPtr self = Handle;
            if (through == _clickThrough && self == _clickThroughHandle)
            {
                return;
            }

            int style = Native.GetWindowExStyle(self);
            int updated = through
                ? style | Native.WS_EX_TRANSPARENT
                : style & ~Native.WS_EX_TRANSPARENT;

            if (updated != style)
            {
                Native.SetWindowExStyle(self, updated);
            }

            _clickThrough = through;
            _clickThroughHandle = self;
        }

        /// <summary>
        /// Starts or stops following the taskbar. Off, the widget is hidden and nothing watches.
        /// </summary>
        public void SetFollowing(bool following)
        {
            if (following == _watching)
            {
                if (!following)
                {
                    HideWidget();
                }

                return;
            }

            _watching = following;

            if (following)
            {
                _watcher = new Thread(Watch);
                _watcher.IsBackground = true;
                _watcher.Name = "taskbar-follow";
                _watcher.Start();
                return;
            }

            Thread watcher = _watcher;
            _watcher = null;
            if (watcher != null)
            {
                // A frame at worst; the thread checks the flag every time round.
                watcher.Join(250);
            }

            HideWidget();
        }

        /// <summary>
        /// Watches the taskbar's rectangle on the compositor's clock, and pokes the UI thread only
        /// when it has actually changed.
        ///
        /// Windows animates an auto-hiding taskbar over roughly 185 ms, stepping its rectangle
        /// about every 25 ms, and the widget is only as smooth as the interval it samples that on.
        /// A WinForms timer cannot sample it: WM_TIMER is rounded up to whole system ticks and
        /// delivered only when the queue is otherwise empty, so a 16 ms timer measured a median
        /// period of 30 ms here and a tail past 90 ms — the widget skipped a third of the
        /// taskbar's steps and arrived late for the rest. DwmFlush blocks until the compositor
        /// finishes its next frame, which is both the fastest cadence worth having (measured 4 ms
        /// on a 240 Hz screen, 16 ms on a 60 Hz one: exactly one frame either way) and the cheapest
        /// way to wait for it, since the thread is asleep in between rather than polling a clock.
        ///
        /// It only watches. Everything that moves or paints the window stays on the UI thread,
        /// reached by a posted message — which, unlike WM_TIMER, is picked up as soon as the
        /// thread returns to its message loop.
        /// </summary>
        private void Watch()
        {
            IntPtr tray = IntPtr.Zero;
            IntPtr notify = IntPtr.Zero;
            Native.RECT lastBar = new Native.RECT();
            Native.RECT lastAnchor = new Native.RECT();
            int retryTick = 0;
            int movedTick = unchecked(Environment.TickCount - AnimationTailMs);

            while (_watching)
            {
                // Frame by frame only while something is actually moving. A still taskbar does not
                // need watching at frame rate, and on a fast screen that is most of the wakeups
                // this thread would ever make.
                if (unchecked(Environment.TickCount - movedTick) < AnimationTailMs)
                {
                    WaitForFrame();
                }
                else
                {
                    Thread.Sleep(IdleIntervalMs);
                }

                if (tray == IntPtr.Zero || !Native.IsWindow(tray))
                {
                    // No taskbar: Explorer is restarting, or has not started yet. Looking for it
                    // every frame would be the one wasteful thing this loop does.
                    if (unchecked(Environment.TickCount - retryTick) < 0)
                    {
                        continue;
                    }

                    retryTick = unchecked(Environment.TickCount + 500);
                    tray = Native.FindWindow("Shell_TrayWnd", null);
                    notify = IntPtr.Zero;
                    if (tray == IntPtr.Zero)
                    {
                        continue;
                    }
                }

                if (notify == IntPtr.Zero || !Native.IsWindow(notify))
                {
                    notify = Native.FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
                }

                Native.RECT bar;
                if (!Native.GetWindowRect(tray, out bar))
                {
                    tray = IntPtr.Zero;
                    continue;
                }

                // The notification area is watched as well as the bar: icons come and go without
                // the taskbar moving at all, and that shifts where the readout has to sit.
                Native.RECT anchor = new Native.RECT();
                if (notify != IntPtr.Zero)
                {
                    Native.GetWindowRect(notify, out anchor);
                }

                if (Same(bar, lastBar) && Same(anchor, lastAnchor))
                {
                    continue;
                }

                lastBar = bar;
                lastAnchor = anchor;
                movedTick = Environment.TickCount;
                Poke();
            }
        }

        /// <summary>
        /// Asks the UI thread for a <see cref="Sync"/>, at most one outstanding at a time: if the
        /// thread is busy, a backlog of posts would only make it repeat work it has already caught
        /// up on.
        /// </summary>
        private void Poke()
        {
            IntPtr target = _followTarget;
            if (target == IntPtr.Zero)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _followPending, 1, 0) != 0)
            {
                return;
            }

            if (!Native.PostMessage(target, WmFollow, IntPtr.Zero, IntPtr.Zero))
            {
                // The window went away between the read and the post; let the next frame retry.
                Interlocked.Exchange(ref _followPending, 0);
            }
        }

        private static bool Same(Native.RECT a, Native.RECT b)
        {
            return a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;
        }

        /// <summary>
        /// Waits out one composition frame. Without a compositor DwmFlush returns immediately, so
        /// the wait is timed and backed by a sleep — otherwise this loop would spin a core.
        /// </summary>
        private static void WaitForFrame()
        {
            long before = Stopwatch.GetTimestamp();
            if (!Native.DwmFlush())
            {
                Thread.Sleep(UncomposedFrameMs);
                return;
            }

            if (Stopwatch.GetTimestamp() - before < Stopwatch.Frequency / 1000)
            {
                Thread.Sleep(1);
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmFollow)
            {
                Interlocked.Exchange(ref _followPending, 0);
                if (_watching)
                {
                    Sync();
                }

                return;
            }

            base.WndProc(ref m);
        }

        /// <summary>
        /// Hands the taskbar ownership of this window.
        ///
        /// Opening Start moves Shell_TrayWnd into a higher z-order band — band 1 to band 6,
        /// measurably — so that the bar draws over the menu's own backdrop. A band outranks
        /// WS_EX_TOPMOST outright: while the menu is open there is no z-order this process can ask
        /// for that puts anything above the bar, so the readout goes under it and the taskbar looks
        /// like it lost the numbers. Re-asserting topmost cannot help, and SetWindowBand — the
        /// call that would promote us to match — is denied to anything without UIAccess.
        ///
        /// An owned window, though, is always ordered above its owner, whatever band the owner is
        /// in. So the widget rides the taskbar's promotion instead of trying to out-rank it, which
        /// covers Start, search, the notification centre and anything else that raises the bar.
        /// </summary>
        private void EnsureOwnedByTaskbar()
        {
            if (_trayHandle == IntPtr.Zero)
            {
                return;
            }

            IntPtr self = Handle;

            // Destroying a window destroys everything it owns, so an Explorer restart takes this
            // window with the taskbar and WinForms hands back a fresh one. That window has never
            // been positioned or shown.
            if (self != _ownedHandle)
            {
                _ownedHandle = self;
                _ownedTo = IntPtr.Zero;
                _placed = false;
                _followTarget = self;
            }

            if (_trayHandle == _ownedTo && Native.GetWindowOwner(self) == _trayHandle)
            {
                return;
            }

            Native.SetWindowOwner(self, _trayHandle);
            _ownedTo = _trayHandle;
        }

        private void HideWidget()
        {
            if (Visible)
            {
                Visible = false;
            }

            _placed = false;
        }

        /// <summary>
        /// Locates the primary taskbar and the notification area inside it. Both windows still exist
        /// on Windows 11 even though the taskbar itself is now XAML.
        ///
        /// <paramref name="visibleHeight"/> is how much of the bar is on screen — a parked
        /// auto-hiding taskbar keeps a two pixel sliver rather than moving away, so the rectangle
        /// alone does not tell you. It never gates positioning: hiding on a threshold is exactly
        /// what makes the widget pop instead of slide. The screen edge does the clipping.
        /// </summary>
        private bool TryGetTaskbar(out Native.RECT taskbar, out Native.RECT anchor, out int visibleHeight)
        {
            taskbar = new Native.RECT();
            anchor = new Native.RECT();
            visibleHeight = 0;

            // Cached: at frame rate, re-finding the window every tick is the only part of this that
            // would cost anything.
            if (_trayHandle == IntPtr.Zero || !Native.IsWindow(_trayHandle))
            {
                _trayHandle = Native.FindWindow("Shell_TrayWnd", null);
                _notifyHandle = IntPtr.Zero;
            }

            IntPtr tray = _trayHandle;
            if (tray == IntPtr.Zero || !Native.IsWindowVisible(tray))
            {
                return false;
            }

            if (!Native.GetWindowRect(tray, out taskbar))
            {
                return false;
            }

            // Only a horizontal taskbar has spare width beside the clock.
            if (taskbar.Width < taskbar.Height * 3)
            {
                return false;
            }

            Rectangle bounds = Screen.PrimaryScreen.Bounds;
            visibleHeight = Math.Max(
                0,
                Math.Min(taskbar.Bottom, bounds.Bottom) - Math.Max(taskbar.Top, bounds.Top));

            if (_notifyHandle == IntPtr.Zero || !Native.IsWindow(_notifyHandle))
            {
                _notifyHandle = Native.FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
            }

            if (_notifyHandle != IntPtr.Zero && Native.GetWindowRect(_notifyHandle, out anchor) && anchor.Width > 0)
            {
                return true;
            }

            // No notification area found: fall back to the right edge of the taskbar.
            anchor = taskbar;
            anchor.Left = taskbar.Right - 8;
            return true;
        }

        /// <summary>
        /// Sizes type to the taskbar the way the clock does: two stacked lines filling the bar's
        /// height. The width is fixed to the widest reading so the text never jitters as numbers
        /// change, and never shoves itself around the taskbar once a second.
        /// </summary>
        private void EnsureFont(int taskbarHeight)
        {
            int lineHeight = (taskbarHeight - (VerticalPadding * 2)) / 2;
            int pixels = (int)Math.Round(lineHeight * 0.78);
            pixels = Math.Max(11, Math.Min(26, pixels));

            if (_font != null && pixels == _fontPixels && _measuredWidth > 0)
            {
                return;
            }

            if (_font != null)
            {
                _font.Dispose();
            }

            _fontPixels = pixels;
            _font = MakeFont(pixels);

            using (Bitmap probe = new Bitmap(1, 1))
            using (Graphics g = Graphics.FromImage(probe))
            {
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

                // The widest reading in either unit mode, so the box never has to resize.
                float widest = 0f;
                string[] samples = { "888.8 MB/s", "888.8 Mbps", "888.8 KB/s" };
                foreach (string sample in samples)
                {
                    float w = g.MeasureString(sample, _font).Width;
                    if (w > widest)
                    {
                        widest = w;
                    }
                }

                _measuredWidth = (int)Math.Ceiling(widest) + ArrowWidth() + (SidePadding * 2);
            }
        }

        // Windows 11 sets the clock in Segoe UI Variable; Windows 10 has only Segoe UI.
        private static Font MakeFont(int pixels)
        {
            string[] preferred = { "Segoe UI Variable Text", "Segoe UI", "Tahoma" };
            foreach (string name in preferred)
            {
                try
                {
                    using (FontFamily family = new FontFamily(name))
                    {
                        return new Font(family, pixels, FontStyle.Regular, GraphicsUnit.Pixel);
                    }
                }
                catch (ArgumentException)
                {
                }
            }

            return new Font(FontFamily.GenericSansSerif, pixels, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        private int ArrowWidth()
        {
            return (int)Math.Round(_fontPixels * 0.62) + 6;
        }

        private void RenderFrame(int width, int height, int screenX, int screenY)
        {
            using (Bitmap bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.Clear(Color.Transparent);

                    // A layered window is hit-tested by its alpha channel, so fully transparent
                    // pixels pass clicks through to the taskbar — which would leave only the glyphs
                    // themselves clickable, and the gaps between them dead. An alpha of 1 is
                    // invisible against any background but still counts as the window.
                    using (SolidBrush hitTest = new SolidBrush(Color.FromArgb(1, 0, 0, 0)))
                    {
                        g.FillRectangle(hitTest, 0, 0, width, height);
                    }

                    g.SmoothingMode = SmoothingMode.AntiAlias;

                    // Sub-pixel AA needs an opaque backdrop; a layered window has none, so grey-scale
                    // anti-aliasing is what keeps the glyph edges clean over the taskbar.
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                    float lineHeight = (height - (VerticalPadding * 2)) / 2f;
                    DrawLine(g, true, _down, VerticalPadding, lineHeight, width);
                    DrawLine(g, false, _up, VerticalPadding + lineHeight, lineHeight, width);
                }

                Commit(bitmap, screenX, screenY);
            }
        }

        private void DrawLine(Graphics g, bool download, double bytesPerSecond, float top, float lineHeight, int width)
        {
            Color accent = download ? _palette.Download : _palette.Upload;
            float arrow = _fontPixels * 0.62f;
            float centre = top + (lineHeight / 2f);

            using (SolidBrush brush = new SolidBrush(accent))
            {
                float half = arrow / 2f;
                float x = SidePadding;
                PointF[] triangle = download
                    ? new PointF[]
                    {
                        new PointF(x, centre - (half * 0.7f)),
                        new PointF(x + arrow, centre - (half * 0.7f)),
                        new PointF(x + half, centre + (half * 0.9f))
                    }
                    : new PointF[]
                    {
                        new PointF(x, centre + (half * 0.7f)),
                        new PointF(x + arrow, centre + (half * 0.7f)),
                        new PointF(x + half, centre - (half * 0.9f))
                    };
                g.FillPolygon(brush, triangle);
            }

            string text = Fmt.SpeedLong(bytesPerSecond, _units);
            SizeF size = g.MeasureString(text, _font);
            float textX = SidePadding + ArrowWidth();
            float textY = centre - (size.Height / 2f);

            using (SolidBrush brush = new SolidBrush(_palette.Text))
            {
                g.DrawString(text, _font, brush, textX, textY);
            }
        }

        /// <summary>Pushes the bitmap to the layered window and keeps it above the taskbar.</summary>
        private void Commit(Bitmap bitmap, int screenX, int screenY)
        {
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr memoryDc = Native.CreateCompatibleDC(screenDc);
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr previous = IntPtr.Zero;

            try
            {
                hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
                previous = Native.SelectObject(memoryDc, hBitmap);

                Native.SIZE size = new Native.SIZE(bitmap.Width, bitmap.Height);
                Native.POINT source = new Native.POINT(0, 0);
                Native.POINT destination = new Native.POINT(screenX, screenY);

                Native.BLENDFUNCTION blend = new Native.BLENDFUNCTION();
                blend.BlendOp = Native.AC_SRC_OVER;
                blend.BlendFlags = 0;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = Native.AC_SRC_ALPHA;

                if (!Visible)
                {
                    Visible = true;
                }

                Native.UpdateLayeredWindow(
                    Handle, screenDc, ref destination, ref size, memoryDc, ref source, 0, ref blend, Native.ULW_ALPHA);

                // First placement: the window has to be shown and given its band before anything
                // is on screen. Every move after this one leaves the z-order alone, because the
                // taskbar's ownership is what holds it.
                if (!_placed)
                {
                    Native.SetWindowPos(
                        Handle,
                        Native.HWND_TOPMOST,
                        screenX,
                        screenY,
                        bitmap.Width,
                        bitmap.Height,
                        Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
                    _placed = true;
                }
                else if (Location.X != screenX || Location.Y != screenY)
                {
                    // A repaint that also moves: the position UpdateLayeredWindow just wrote has to
                    // reach the window itself, but the z-order is left alone for the same reason
                    // the move-only path leaves it alone.
                    Native.SetWindowPos(
                        Handle,
                        IntPtr.Zero,
                        screenX,
                        screenY,
                        bitmap.Width,
                        bitmap.Height,
                        Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
                }
            }
            finally
            {
                if (hBitmap != IntPtr.Zero)
                {
                    Native.SelectObject(memoryDc, previous);
                    Native.DeleteObject(hBitmap);
                }

                Native.DeleteDC(memoryDc);
                Native.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            // Nothing to post to until the UI thread has built a window again.
            _followTarget = IntPtr.Zero;
            base.OnHandleDestroyed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SetFollowing(false);

                if (_font != null)
                {
                    _font.Dispose();
                    _font = null;
                }
            }

            base.Dispose(disposing);
        }
    }
}
