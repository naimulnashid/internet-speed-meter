using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace InternetSpeedMeter
{
    /// <summary>Owns the tray icon, the once-a-second sampling loop and the context menu.</summary>
    internal sealed class TrayApp : ApplicationContext
    {
        private const int HistoryLength = 60;
        private const int SampleIntervalMs = 1000;
        private const int HeartbeatIntervalMs = 150;

        private readonly Settings _settings;
        private readonly NetMonitor _monitor = new NetMonitor();
        private readonly IconRenderer _renderer = new IconRenderer();
        private readonly NotifyIcon _tray;
        private readonly Timer _timer;
        private readonly Timer _heartbeat;
        private readonly FlyoutForm _flyout;
        private readonly ContextMenuStrip _menu;

        private readonly List<double> _historyDown = new List<double>();
        private readonly List<double> _historyUp = new List<double>();
        private readonly Recorder _recorder;

        private Palette _palette;
        private int _iconSize;
        private double _totalDown;
        private double _totalUp;
        private DateTime _sessionStart = DateTime.Now;
        private Reading _last = new Reading();
        private Icon _liveIcon;
        private Icon _logoIcon;
        private Font _menuBoldFont;
        private readonly TaskbarWidget _widget;

        public TrayApp()
        {
            _settings = Settings.Load();
            _palette = Palette.For(_settings.Theme);
            _iconSize = Native.TrayIconSize();
            _recorder = new Recorder(_settings);

            _flyout = new FlyoutForm();
            _flyout.SetPalette(_palette);

            _widget = new TaskbarWidget();
            _widget.SetPalette(_palette);
            _widget.SetUnits(_settings.Units);
            _widget.SetSide(_settings.Side);
            _widget.LeftClicked += delegate { ToggleFlyout(); };
            _widget.RightClicked += delegate { ShowWidgetMenu(); };

            _menu = new ContextMenuStrip();
            _menu.Opening += OnMenuOpening;

            // No ContextMenuStrip on the icon: the menu is opened by hand instead, across the
            // press and the release. See OnTrayMouseDown.
            _tray = new NotifyIcon();
            _tray.Visible = true;
            _tray.Text = "Internet Speed Meter";
            _tray.MouseDown += OnTrayMouseDown;
            _tray.MouseUp += OnTrayMouseUp;

            _timer = new Timer();
            _timer.Interval = SampleIntervalMs;
            _timer.Tick += OnTick;

            // The widget follows the taskbar's own motion from a watcher thread paced by the
            // compositor, so this is not what keeps it in step: it is the slow heartbeat behind
            // that, re-asserting the z-order and catching anything that changes the placement
            // without moving the taskbar at all.
            _heartbeat = new Timer();
            _heartbeat.Interval = HeartbeatIntervalMs;
            _heartbeat.Tick += delegate
            {
                if (_settings.TaskbarText)
                {
                    _widget.Sync();
                }
            };

            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

            // Prime the counters so the first visible reading is a real one second window.
            _monitor.Sample(_settings.Adapter);
            Redraw();
            _timer.Start();
            _heartbeat.Start();
        }

        private void OnTick(object sender, EventArgs e)
        {
            Reading reading = _monitor.Sample(_settings.Adapter);
            _last = reading;

            _recorder.Add(reading);

            _totalDown += reading.DownBytes;
            _totalUp += reading.UpBytes;

            Push(_historyDown, reading.DownBytesPerSecond);
            Push(_historyUp, reading.UpBytesPerSecond);

            Redraw();
        }

        private static void Push(List<double> history, double value)
        {
            history.Add(value);
            while (history.Count > HistoryLength)
            {
                history.RemoveAt(0);
            }
        }

        private void Redraw()
        {
            // The taskbar text is the primary readout when it can be placed; if the taskbar is
            // auto-hidden, vertical, or simply out of room, the tray icon takes over the numbers so
            // the meter is never silently blank.
            WidgetPlacement placement = WidgetPlacement.Unavailable;
            if (_settings.TaskbarText)
            {
                _widget.SetPalette(_palette);
                _widget.SetUnits(_settings.Units);
                _widget.SetSide(_settings.Side);
                _widget.SetFollowing(true);
                placement = _widget.Update(_last.DownBytesPerSecond, _last.UpBytesPerSecond);
            }
            else
            {
                _widget.SetFollowing(false);
            }

            // An auto-hidden taskbar hides the tray icon too, so that is not a reason to duplicate
            // the numbers there; only a taskbar that can never host the text is.
            bool numbersInTray = _settings.TrayNumbers || placement == WidgetPlacement.Unavailable;
            if (!numbersInTray)
            {
                ShowLogoIcon();
                UpdateTooltip();
                return;
            }

            string downValue;
            string downUnit;
            string upValue;
            string upUnit;
            Fmt.SpeedShort(_last.DownBytesPerSecond, _settings.Units, out downValue, out downUnit);
            Fmt.SpeedShort(_last.UpBytesPerSecond, _settings.Units, out upValue, out upUnit);

            string downText = _settings.ShowUnitOnIcon ? downValue + downUnit : downValue;
            string upText = _settings.ShowUnitOnIcon ? upValue + upUnit : upValue;

            Icon icon;
            if (_settings.Layout == IconLayout.DownloadOnly)
            {
                icon = _renderer.RenderSingle(downText, _palette.Download, _iconSize);
            }
            else if (_settings.Layout == IconLayout.UploadOnly)
            {
                icon = _renderer.RenderSingle(upText, _palette.Upload, _iconSize);
            }
            else
            {
                string topText = _settings.UploadOnTop ? upText : downText;
                string bottomText = _settings.UploadOnTop ? downText : upText;
                Color topColor = _settings.UploadOnTop ? _palette.Upload : _palette.Download;
                Color bottomColor = _settings.UploadOnTop ? _palette.Download : _palette.Upload;

                icon = _renderer.Render(topText, bottomText, topColor, bottomColor, _iconSize);
            }

            if (icon != null)
            {
                _tray.Icon = icon;
                if (_liveIcon != null)
                {
                    _liveIcon.Dispose();
                }

                _liveIcon = icon;
            }

            UpdateTooltip();
        }

        /// <summary>Static app icon, used while the numbers live on the taskbar instead.</summary>
        private void ShowLogoIcon()
        {
            if (_logoIcon == null)
            {
                try
                {
                    _logoIcon = Icon.ExtractAssociatedIcon(AutoStart.ExecutablePath);
                }
                catch (Exception)
                {
                    _logoIcon = SystemIcons.Application;
                }
            }

            if (!ReferenceEquals(_tray.Icon, _logoIcon))
            {
                _tray.Icon = _logoIcon;
                if (_liveIcon != null)
                {
                    _liveIcon.Dispose();
                    _liveIcon = null;
                }
            }
        }

        private void UpdateTooltip()
        {
            // NotifyIcon tooltips are capped at 63 characters, so keep this tight.
            string tip = "↓ " + Fmt.SpeedLong(_last.DownBytesPerSecond, _settings.Units) +
                         "   ↑ " + Fmt.SpeedLong(_last.UpBytesPerSecond, _settings.Units) +
                         "\n" + Fmt.Ellipsize(_last.SourceName, 28);
            _tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;

            if (_flyout.Visible)
            {
                PushFlyoutData();
            }
        }

        private void PushFlyoutData()
        {
            _flyout.UpdateData(
                _last.SourceName,
                _last.DownBytesPerSecond,
                _last.UpBytesPerSecond,
                _totalDown,
                _totalUp,
                _sessionStart,
                _settings.Units,
                _historyDown.ToArray(),
                _historyUp.ToArray());
        }

        /// <summary>
        /// Opens the menu from the taskbar readout.
        ///
        /// Nothing but the showing happens here: a menu is dismissed by the click that lands
        /// outside it, and only a foreground application is told about a click that lands in
        /// another one, so the readout has already taken the foreground by the time this runs. It
        /// does that on the button going down rather than coming up, for reasons that belong with
        /// the readout -- see <see cref="TaskbarWidget.OnMouseDown"/>.
        /// </summary>
        private void ShowWidgetMenu()
        {
            _menu.Show(Cursor.Position);
        }

        /// <summary>
        /// Takes the foreground on the press, so that the menu the release opens is a foreground
        /// window's menu.
        ///
        /// NotifyIcon will open the menu itself, given a ContextMenuStrip, and it does it in the
        /// one order that costs a click: it asks for the foreground and shows the menu in the same
        /// breath, both on the button coming up. The activation is still in flight when the menu
        /// appears -- it reaches this thread as messages, which are only read once the thread is
        /// back in its message loop -- and a dropdown treats an activation landing under it as the
        /// user having clicked elsewhere, so it closes again immediately. The first right-click
        /// after the application lost the foreground opened nothing at all; the second, with the
        /// foreground already ours and nothing left to ask for, opened the menu and kept it.
        ///
        /// This is the same fault, from the same cause, as the taskbar readout had, and it takes
        /// the same fix: ask on the press, show on the release, and the activation is long since
        /// finished before there is a menu for it to disturb. The window asked for is the readout's
        /// -- the same one the readout's own menu uses -- so that the two routes into this menu
        /// leave the application in the same state.
        /// </summary>
        private void OnTrayMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                Native.SetForegroundWindow(_widget.Handle);
            }
        }

        private void OnTrayMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                _menu.Show(Cursor.Position);
                return;
            }

            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            ToggleFlyout();
        }

        private void ToggleFlyout()
        {
            if (_flyout.Visible)
            {
                _flyout.Hide();
                return;
            }

            // The click that reaches the tray may be the same one that just dismissed the panel.
            if ((DateTime.UtcNow - _flyout.HiddenAt).TotalMilliseconds < 300)
            {
                return;
            }

            PushFlyoutData();
            _flyout.ShowNear(Cursor.Position);
        }

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category != UserPreferenceCategory.General &&
                e.Category != UserPreferenceCategory.Color &&
                e.Category != UserPreferenceCategory.VisualStyle)
            {
                return;
            }

            ApplyTheme();
        }

        private void ApplyTheme()
        {
            _palette = Palette.For(_settings.Theme);
            _flyout.SetPalette(_palette);
            Redraw();
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            int size = Native.TrayIconSize();
            if (size != _iconSize)
            {
                _iconSize = size;
                Redraw();
            }
        }

        // The adapter list is rebuilt on every open so hotplugged Wi-Fi/Ethernet shows up.
        private void OnMenuOpening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // Items are recreated every time, so the previous set has to be released explicitly:
            // Items.Clear() detaches them but does not dispose them.
            ToolStripItem[] previous = new ToolStripItem[_menu.Items.Count];
            _menu.Items.CopyTo(previous, 0);
            _menu.Items.Clear();
            foreach (ToolStripItem item in previous)
            {
                item.Dispose();
            }

            if (_menuBoldFont == null)
            {
                _menuBoldFont = new Font(_menu.Font, FontStyle.Bold);
            }

            ToolStripMenuItem details = new ToolStripMenuItem("Details…", null, delegate { ToggleFlyout(); });
            details.Font = _menuBoldFont;
            _menu.Items.Add(details);
            _menu.Items.Add(new ToolStripSeparator());

            _menu.Items.Add(BuildDisplayMenu());
            _menu.Items.Add(BuildAdapterMenu());
            _menu.Items.Add(BuildUnitsMenu());
            _menu.Items.Add(BuildIconMenu());

            _menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem startup = new ToolStripMenuItem("Start with Windows", null, OnToggleStartup);
            startup.Checked = AutoStart.IsEnabled();
            _menu.Items.Add(startup);

            _menu.Items.Add(new ToolStripMenuItem("Reset session counters", null, delegate
            {
                _totalDown = 0;
                _totalUp = 0;
                _sessionStart = DateTime.Now;
                _historyDown.Clear();
                _historyUp.Clear();
                Redraw();
            }));

            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(new ToolStripMenuItem("Exit", null, delegate { ExitApp(); }));
        }

        private ToolStripMenuItem BuildDisplayMenu()
        {
            ToolStripMenuItem root = new ToolStripMenuItem("Show speeds on");

            ToolStripMenuItem taskbar = new ToolStripMenuItem("Taskbar text  (clock size)", null, delegate
            {
                _settings.TaskbarText = !_settings.TaskbarText;
                _settings.Save();
                Redraw();
            });
            taskbar.Checked = _settings.TaskbarText;
            root.DropDownItems.Add(taskbar);

            ToolStripMenuItem tray = new ToolStripMenuItem("Tray icon numbers", null, delegate
            {
                _settings.TrayNumbers = !_settings.TrayNumbers;
                _settings.Save();
                Redraw();
            });
            tray.Checked = _settings.TrayNumbers;
            root.DropDownItems.Add(tray);

            root.DropDownItems.Add(new ToolStripSeparator());

            ToolStripMenuItem position = new ToolStripMenuItem("Taskbar text position");
            position.DropDownItems.Add(MakeSideItem("Left end", TaskbarSide.Left));
            position.DropDownItems.Add(MakeSideItem("Right, beside the clock", TaskbarSide.Right));
            root.DropDownItems.Add(position);

            return root;
        }

        private ToolStripMenuItem MakeSideItem(string label, TaskbarSide side)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(label);
            item.Checked = _settings.Side == side;
            TaskbarSide captured = side;
            item.Click += delegate
            {
                _settings.Side = captured;
                _settings.Save();
                Redraw();
            };
            return item;
        }

        private ToolStripMenuItem BuildAdapterMenu()
        {
            ToolStripMenuItem root = new ToolStripMenuItem("Adapter");

            root.DropDownItems.Add(MakeAdapterItem("Automatic (busiest adapter)", Settings.AdapterAuto));
            root.DropDownItems.Add(MakeAdapterItem("All adapters (combined)", Settings.AdapterAll));
            root.DropDownItems.Add(new ToolStripSeparator());

            List<AdapterInfo> adapters = _monitor.ListAdapters();
            if (adapters.Count == 0)
            {
                ToolStripMenuItem none = new ToolStripMenuItem("No connected adapters");
                none.Enabled = false;
                root.DropDownItems.Add(none);
            }
            else
            {
                foreach (AdapterInfo adapter in adapters)
                {
                    root.DropDownItems.Add(MakeAdapterItem(adapter.Label, adapter.Id));
                }
            }

            return root;
        }

        private ToolStripMenuItem MakeAdapterItem(string label, string id)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(label);
            item.Checked = _settings.Adapter == id;
            string captured = id;
            item.Click += delegate
            {
                _settings.Adapter = captured;
                _settings.Save();
                _historyDown.Clear();
                _historyUp.Clear();
                Redraw();
            };
            return item;
        }

        private ToolStripMenuItem BuildUnitsMenu()
        {
            ToolStripMenuItem root = new ToolStripMenuItem("Units");
            root.DropDownItems.Add(MakeUnitItem("Bytes  (KB/s, MB/s)", UnitMode.Bytes));
            root.DropDownItems.Add(MakeUnitItem("Bits  (Kbps, Mbps)", UnitMode.Bits));
            return root;
        }

        private ToolStripMenuItem MakeUnitItem(string label, UnitMode mode)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(label);
            item.Checked = _settings.Units == mode;
            UnitMode captured = mode;
            item.Click += delegate
            {
                _settings.Units = captured;
                _settings.Save();
                Redraw();
            };
            return item;
        }

        private ToolStripMenuItem BuildIconMenu()
        {
            ToolStripMenuItem root = new ToolStripMenuItem("Icon");

            ToolStripMenuItem layout = new ToolStripMenuItem("Layout");
            layout.DropDownItems.Add(MakeLayoutItem("Download and upload", IconLayout.Both));
            layout.DropDownItems.Add(MakeLayoutItem("Download only  (bigger text)", IconLayout.DownloadOnly));
            layout.DropDownItems.Add(MakeLayoutItem("Upload only  (bigger text)", IconLayout.UploadOnly));
            root.DropDownItems.Add(layout);
            root.DropDownItems.Add(new ToolStripSeparator());

            ToolStripMenuItem units = new ToolStripMenuItem("Show unit letters", null, delegate
            {
                _settings.ShowUnitOnIcon = !_settings.ShowUnitOnIcon;
                _settings.Save();
                Redraw();
            });
            units.Checked = _settings.ShowUnitOnIcon;
            root.DropDownItems.Add(units);

            ToolStripMenuItem swap = new ToolStripMenuItem("Upload on top", null, delegate
            {
                _settings.UploadOnTop = !_settings.UploadOnTop;
                _settings.Save();
                Redraw();
            });
            swap.Checked = _settings.UploadOnTop;
            root.DropDownItems.Add(swap);

            root.DropDownItems.Add(new ToolStripSeparator());

            ToolStripMenuItem theme = new ToolStripMenuItem("Text colour");
            theme.DropDownItems.Add(MakeThemeItem("Match taskbar", ThemeMode.Auto));
            theme.DropDownItems.Add(MakeThemeItem("Light text (dark taskbar)", ThemeMode.Dark));
            theme.DropDownItems.Add(MakeThemeItem("Dark text (light taskbar)", ThemeMode.Light));
            root.DropDownItems.Add(theme);

            return root;
        }

        private ToolStripMenuItem MakeLayoutItem(string label, IconLayout layout)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(label);
            item.Checked = _settings.Layout == layout;
            IconLayout captured = layout;
            item.Click += delegate
            {
                _settings.Layout = captured;
                _settings.Save();
                Redraw();
            };
            return item;
        }

        private ToolStripMenuItem MakeThemeItem(string label, ThemeMode mode)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(label);
            item.Checked = _settings.Theme == mode;
            ThemeMode captured = mode;
            item.Click += delegate
            {
                _settings.Theme = captured;
                _settings.Save();
                ApplyTheme();
            };
            return item;
        }

        private void OnToggleStartup(object sender, EventArgs e)
        {
            bool enable = !AutoStart.IsEnabled();
            if (!AutoStart.Set(enable))
            {
                MessageBox.Show(
                    "Could not update the Windows startup entry. The registry key may be managed by your organisation.",
                    "Internet Speed Meter",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        private void ExitApp()
        {
            _timer.Stop();
            _heartbeat.Stop();
            _widget.SetFollowing(false);
            _tray.Visible = false;
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

                _timer.Dispose();
                _heartbeat.Dispose();

                // After the timer, so nothing can arrive mid-flush, and before anything that can
                // throw: a clean exit is the only chance the partial minute has of being written.
                _recorder.Dispose();

                _tray.Visible = false;
                _tray.Dispose();
                _menu.Dispose();

                if (_menuBoldFont != null)
                {
                    _menuBoldFont.Dispose();
                    _menuBoldFont = null;
                }

                _flyout.Dispose();
                _widget.Dispose();

                if (_liveIcon != null)
                {
                    _liveIcon.Dispose();
                    _liveIcon = null;
                }

                if (_logoIcon != null && !ReferenceEquals(_logoIcon, SystemIcons.Application))
                {
                    _logoIcon.Dispose();
                    _logoIcon = null;
                }

                _renderer.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
