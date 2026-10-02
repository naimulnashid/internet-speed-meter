using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace InternetSpeedMeter
{
    internal enum UnitMode
    {
        Bytes,
        Bits
    }

    /// <summary>What the icon shows: both rates stacked, or one rate at double the size.</summary>
    internal enum IconLayout
    {
        Both,
        DownloadOnly,
        UploadOnly
    }

    /// <summary>Which end of the taskbar the text sits at.</summary>
    internal enum TaskbarSide
    {
        Left,
        Right
    }

    internal enum ThemeMode
    {
        Auto,
        Light,
        Dark
    }

    /// <summary>Plain key=value settings file stored under %AppData%.</summary>
    internal sealed class Settings
    {
        public const string AdapterAuto = "auto";
        public const string AdapterAll = "all";

        public string Adapter = AdapterAuto;
        public UnitMode Units = UnitMode.Bytes;
        public ThemeMode Theme = ThemeMode.Auto;
        public bool ShowUnitOnIcon = true;
        public bool UploadOnTop = false;
        public IconLayout Layout = IconLayout.Both;

        // Taskbar text is the readable default; the tray icon then carries the app logo and the
        // menu rather than duplicating the numbers.
        public bool TaskbarText = true;
        public bool TrayNumbers = false;
        public TaskbarSide Side = TaskbarSide.Left;

        // Recording.
        //
        // The default lives under %LocalAppData%, because that is the folder every Windows machine
        // has and the only honest choice for a default: a hard-coded drive letter works on exactly
        // one computer and disables recording silently on every other.
        //
        // It is a compromise, and worth stating plainly. %LocalAppData% is on the system drive,
        // which a Windows reset destroys — so anyone who wants the history to outlive a reset must
        // point LogFolder at another drive. There is still no automatic fallback in either
        // direction: a configured folder that cannot be written disables recording and says so,
        // rather than quietly relocating the history somewhere the reader is not looking.
        //
        // Local rather than roaming: this grows to tens of megabytes a year and has no business
        // following a user between machines. The settings file itself stays in roaming %AppData%.
        public bool Record = true;
        public string LogFolder = DefaultFolder("history");

        // Kept apart from the minute rollups because the two differ in retention and in worth. Raw
        // samples are rewritten every second and dropped after a fortnight, so on a machine where
        // LogFolder has been moved into a synced folder, this one should not follow — syncing a
        // file that is deleted a fortnight later uploads a copy of it forever for nothing.
        //
        // Empty is a supported value, meaning minute rollups only.
        public string RawFolder = DefaultFolder("raw");
        public int RawRetentionDays = 14;

        /// <summary>
        /// Combined down+up rate at or above which a second counts as "active", in bytes.
        ///
        /// Every average-while-active figure on the dashboard moves when this moves, which is why
        /// it is recorded per minute rather than decided at read time — changing it later must not
        /// silently rewrite what the past looked like.
        /// </summary>
        public long ActiveThresholdBps = 50 * 1024;

        public static string Folder
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "InternetSpeedMeter");
            }
        }

        /// <summary>A recording folder under %LocalAppData%, resolved on this machine.</summary>
        private static string DefaultFolder(string leaf)
        {
            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (local.Length > 0)
                {
                    return Path.Combine(Path.Combine(local, "InternetSpeedMeter"), leaf);
                }
            }
            catch (Exception)
            {
            }

            // A profile with no LocalAppData is not a machine to start writing guesses on.
            return string.Empty;
        }

        public static string FilePath
        {
            get { return Path.Combine(Folder, "settings.ini"); }
        }

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                if (!File.Exists(FilePath))
                {
                    return s;
                }

                foreach (string raw in File.ReadAllLines(FilePath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';')
                    {
                        continue;
                    }

                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                    {
                        continue;
                    }

                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string value = line.Substring(eq + 1).Trim();

                    switch (key)
                    {
                        case "adapter":
                            s.Adapter = value.Length == 0 ? AdapterAuto : value;
                            break;
                        case "units":
                            s.Units = value.Equals("bits", StringComparison.OrdinalIgnoreCase) ? UnitMode.Bits : UnitMode.Bytes;
                            break;
                        case "theme":
                            if (value.Equals("light", StringComparison.OrdinalIgnoreCase))
                            {
                                s.Theme = ThemeMode.Light;
                            }
                            else if (value.Equals("dark", StringComparison.OrdinalIgnoreCase))
                            {
                                s.Theme = ThemeMode.Dark;
                            }
                            else
                            {
                                s.Theme = ThemeMode.Auto;
                            }
                            break;
                        case "showuniticon":
                            s.ShowUnitOnIcon = ParseBool(value, true);
                            break;
                        case "uploadontop":
                            s.UploadOnTop = ParseBool(value, false);
                            break;
                        case "taskbartext":
                            s.TaskbarText = ParseBool(value, true);
                            break;
                        case "traynumbers":
                            s.TrayNumbers = ParseBool(value, false);
                            break;
                        case "side":
                            s.Side = value.Equals("right", StringComparison.OrdinalIgnoreCase)
                                ? TaskbarSide.Right
                                : TaskbarSide.Left;
                            break;
                        case "record":
                            s.Record = ParseBool(value, true);
                            break;
                        case "logfolder":
                            s.LogFolder = ExpandFolder(value, s.LogFolder);
                            break;
                        case "rawfolder":
                            // Empty is meaningful here — it is how minutes-only is asked for — so
                            // it overrides the default instead of falling back to it.
                            s.RawFolder = Environment.ExpandEnvironmentVariables(value);
                            break;
                        case "rawretentiondays":
                            s.RawRetentionDays = ParseInt(value, s.RawRetentionDays, 0, 3650);
                            break;
                        case "activethresholdbps":
                            s.ActiveThresholdBps = ParseInt(value, (int)s.ActiveThresholdBps, 0, int.MaxValue);
                            break;
                        case "layout":
                            if (value.Equals("down", StringComparison.OrdinalIgnoreCase))
                            {
                                s.Layout = IconLayout.DownloadOnly;
                            }
                            else if (value.Equals("up", StringComparison.OrdinalIgnoreCase))
                            {
                                s.Layout = IconLayout.UploadOnly;
                            }
                            else
                            {
                                s.Layout = IconLayout.Both;
                            }
                            break;
                    }
                }
            }
            catch (Exception)
            {
                // A damaged settings file must never stop the app from starting.
            }

            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                List<string> lines = new List<string>();
                lines.Add("# Internet Speed Meter settings");
                lines.Add("adapter=" + Adapter);
                lines.Add("units=" + (Units == UnitMode.Bits ? "bits" : "bytes"));
                lines.Add("theme=" + Theme.ToString().ToLowerInvariant());
                lines.Add("showuniticon=" + (ShowUnitOnIcon ? "1" : "0"));
                lines.Add("uploadontop=" + (UploadOnTop ? "1" : "0"));
                lines.Add("taskbartext=" + (TaskbarText ? "1" : "0"));
                lines.Add("traynumbers=" + (TrayNumbers ? "1" : "0"));
                lines.Add("side=" + (Side == TaskbarSide.Right ? "right" : "left"));
                lines.Add("layout=" + (Layout == IconLayout.DownloadOnly
                    ? "down"
                    : (Layout == IconLayout.UploadOnly ? "up" : "both")));
                lines.Add("record=" + (Record ? "1" : "0"));
                lines.Add("logfolder=" + LogFolder);
                lines.Add("rawfolder=" + RawFolder);
                lines.Add("rawretentiondays=" + RawRetentionDays.ToString(CultureInfo.InvariantCulture));
                lines.Add("activethresholdbps=" + ActiveThresholdBps.ToString(CultureInfo.InvariantCulture));
                File.WriteAllLines(FilePath, lines.ToArray());
            }
            catch (Exception)
            {
                // Read-only profile or roaming hiccup: keep running with in-memory settings.
            }
        }

        private static bool ParseBool(string value, bool fallback)
        {
            if (value.Equals("1", StringComparison.Ordinal) ||
                value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (value.Equals("0", StringComparison.Ordinal) ||
                value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("no", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return fallback;
        }

        /// <summary>
        /// Expands %VARIABLES% in a configured path, so settings.ini can be written portably as
        /// e.g. %LocalAppData%\InternetSpeedMeter\history. An empty value keeps the default,
        /// since blanking the folder the history lives in is far more likely to be an accident
        /// than a request to stop recording — <c>record=0</c> says that unambiguously.
        /// </summary>
        private static string ExpandFolder(string value, string fallback)
        {
            if (value.Length == 0)
            {
                return fallback;
            }

            try
            {
                return Environment.ExpandEnvironmentVariables(value);
            }
            catch (Exception)
            {
                return value;
            }
        }

        private static int ParseInt(string value, int fallback, int min, int max)
        {
            int parsed;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                return fallback;
            }

            if (parsed < min)
            {
                return min;
            }

            return parsed > max ? max : parsed;
        }

        public static string Num(double value, int decimals)
        {
            return value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture);
        }
    }
}
