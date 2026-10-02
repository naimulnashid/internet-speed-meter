using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace InternetSpeedMeter
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--preview-icon")
            {
                IconPreview.Dump(args.Length > 1 ? args[1] : "icon-preview.png");
                return;
            }

            // Ask for DPI awareness before any window exists, so GetSystemMetrics reports the real
            // tray icon size instead of a scaled 96 dpi value.
            try
            {
                Native.SetProcessDPIAware();
            }
            catch (Exception)
            {
            }

            if (args.Length > 0 && args[0] == "--sample")
            {
                int seconds = 10;
                if (args.Length > 1)
                {
                    int parsed;
                    if (int.TryParse(args[1], out parsed) && parsed > 0)
                    {
                        seconds = parsed;
                    }
                }

                RunSample(seconds, args.Length > 2 ? args[2] : null);
                return;
            }

            if (args.Length > 0 && args[0] == "--export-csv")
            {
                RunExport(args);
                return;
            }

            if (args.Length > 0 && args[0] == "--preview-flyout")
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                IconPreview.DumpFlyout(args.Length > 1 ? args[1] : "flyout-preview.png");
                return;
            }

            bool created;
            using (new Mutex(true, @"Local\InternetSpeedMeter.SingleInstance", out created))
            {
                if (!created)
                {
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += OnThreadException;

                using (TrayApp app = new TrayApp())
                {
                    Application.Run(app);
                }
            }
        }

        /// <summary>
        /// Troubleshooting mode: prints one line per second showing which adapter is selected and
        /// what it is doing, without touching the tray. Useful when the icon reads zero and you
        /// need to know whether it is the meter or the network.
        /// </summary>
        private static void RunSample(int seconds, string outputPath)
        {
            Settings settings = Settings.Load();
            NetMonitor monitor = new NetMonitor();
            List<string> lines = new List<string>();

            lines.Add("adapters:");
            foreach (AdapterInfo adapter in monitor.ListAdapters())
            {
                lines.Add("  " + adapter.Label + "  [" + adapter.Id + "]");
            }

            lines.Add("selection: " + settings.Adapter);
            lines.Add("");

            monitor.Sample(settings.Adapter);
            for (int i = 0; i < seconds; i++)
            {
                Thread.Sleep(1000);
                Reading reading = monitor.Sample(settings.Adapter);
                lines.Add(string.Format(
                    "{0}  {1,-22}  down {2,12}  up {3,12}",
                    DateTime.Now.ToString("HH:mm:ss"),
                    Fmt.Ellipsize(reading.SourceName, 22),
                    Fmt.SpeedLong(reading.DownBytesPerSecond, settings.Units),
                    Fmt.SpeedLong(reading.UpBytesPerSecond, settings.Units)));
            }

            string text = string.Join(Environment.NewLine, lines.ToArray());
            if (!string.IsNullOrEmpty(outputPath))
            {
                File.WriteAllText(outputPath, text);
            }

            // A winexe has no console of its own; borrow the caller's when there is one.
            if (Native.AttachParentConsole())
            {
                Console.WriteLine(text);
            }
        }

        /// <summary>
        /// Writes recorded history as CSV:
        ///
        ///   --export-csv &lt;from&gt; &lt;to&gt; [file] [--raw]
        ///
        /// Dates are local and inclusive, and "today" and "yesterday" are accepted for the common
        /// case. Without a file the CSV goes to the calling console, so it can be piped.
        /// </summary>
        private static void RunExport(string[] args)
        {
            string outputPath = null;
            LogExport.Source source = LogExport.Source.Minutes;
            List<string> positional = new List<string>();

            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--raw")
                {
                    source = LogExport.Source.Raw;
                }
                else
                {
                    positional.Add(args[i]);
                }
            }

            DateTime from;
            DateTime to;
            if (positional.Count < 2 ||
                !TryParseDay(positional[0], out from) ||
                !TryParseDay(positional[1], out to))
            {
                Report(
                    "usage: InternetSpeedMeter.exe --export-csv <from> <to> [file] [--raw]" + Environment.NewLine +
                    Environment.NewLine +
                    "  <from> <to>  local dates, inclusive: YYYY-MM-DD, or today / yesterday" + Environment.NewLine +
                    "  file         where to write; omitted, the CSV goes to this console" + Environment.NewLine +
                    "  --raw        one row per second instead of per minute (recent days only)",
                    null);
                return;
            }

            if (to < from)
            {
                DateTime swap = from;
                from = to;
                to = swap;
            }

            if (positional.Count > 2)
            {
                outputPath = positional[2];
            }

            Settings settings = Settings.Load();

            try
            {
                if (!string.IsNullOrEmpty(outputPath))
                {
                    int rows;
                    using (StreamWriter writer = new StreamWriter(outputPath, false))
                    {
                        rows = LogExport.Write(settings, source, from, to, writer);
                    }

                    Report(
                        rows.ToString(CultureInfo.InvariantCulture) + " rows written to " + outputPath,
                        null);
                }
                else
                {
                    StringWriter buffer = new StringWriter(CultureInfo.InvariantCulture);
                    LogExport.Write(settings, source, from, to, buffer);
                    Report(buffer.ToString(), null);
                }
            }
            catch (Exception ex)
            {
                Report("export failed: " + ex.Message, ex);
            }
        }

        /// <summary>Accepts an ISO date, or the two relative days worth spelling.</summary>
        private static bool TryParseDay(string value, out DateTime day)
        {
            if (string.Equals(value, "today", StringComparison.OrdinalIgnoreCase))
            {
                day = DateTime.Now.Date;
                return true;
            }

            if (string.Equals(value, "yesterday", StringComparison.OrdinalIgnoreCase))
            {
                day = DateTime.Now.Date.AddDays(-1);
                return true;
            }

            return DateTime.TryParseExact(
                value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
        }

        /// <summary>
        /// A winexe has no console of its own. Borrow the caller's when there is one, and fall back
        /// to a message box so a double-clicked mistake is not silent.
        /// </summary>
        private static void Report(string text, Exception detail)
        {
            if (Native.AttachParentConsole())
            {
                Console.WriteLine(text);
                return;
            }

            MessageBox.Show(
                detail == null ? text : text + Environment.NewLine + Environment.NewLine + detail,
                "Internet Speed Meter",
                MessageBoxButtons.OK,
                detail == null ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        }

        private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(Settings.Folder);
                File.AppendAllText(
                    Path.Combine(Settings.Folder, "error.log"),
                    DateTime.Now.ToString("u") + Environment.NewLine + e.Exception + Environment.NewLine + Environment.NewLine);
            }
            catch (Exception)
            {
            }

            MessageBox.Show(
                e.Exception.Message + Environment.NewLine + Environment.NewLine +
                "Details were written to " + Path.Combine(Settings.Folder, "error.log"),
                "Internet Speed Meter",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
