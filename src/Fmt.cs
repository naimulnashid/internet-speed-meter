using System;

namespace InternetSpeedMeter
{
    /// <summary>Formatting helpers shared by the tray icon, tooltip and flyout.</summary>
    internal static class Fmt
    {
        private static readonly string[] ByteRateUnits = { "B/s", "KB/s", "MB/s", "GB/s", "TB/s" };
        private static readonly string[] BitRateUnits = { "bps", "Kbps", "Mbps", "Gbps", "Tbps" };
        private static readonly string[] ByteRateShort = { "B", "K", "M", "G", "T" };
        private static readonly string[] BitRateShort = { "b", "K", "M", "G", "T" };
        private static readonly string[] TotalUnits = { "B", "KB", "MB", "GB", "TB", "PB" };

        private static int Scale(double value, double step, string[] units, out double scaled)
        {
            int index = 0;
            scaled = value;
            while (scaled >= step && index < units.Length - 1)
            {
                scaled /= step;
                index++;
            }

            return index;
        }

        /// <summary>Compact value for the tray icon, e.g. "9.4" + "M".</summary>
        public static void SpeedShort(double bytesPerSecond, UnitMode units, out string value, out string unit)
        {
            bool bits = units == UnitMode.Bits;
            double raw = bits ? bytesPerSecond * 8.0 : bytesPerSecond;
            double step = bits ? 1000.0 : 1024.0;
            string[] table = bits ? BitRateShort : ByteRateShort;

            double scaled;
            int index = Scale(raw, step, table, out scaled);

            if (scaled < 0.05)
            {
                value = "0";
            }
            else if (scaled < 10.0 && index > 0)
            {
                value = Settings.Num(scaled, 1);
            }
            else
            {
                value = Settings.Num(scaled, 0);
            }

            unit = table[index];
        }

        /// <summary>Full value for the flyout and tooltip, e.g. "9.42 MB/s".</summary>
        public static string SpeedLong(double bytesPerSecond, UnitMode units)
        {
            bool bits = units == UnitMode.Bits;
            double raw = bits ? bytesPerSecond * 8.0 : bytesPerSecond;
            double step = bits ? 1000.0 : 1024.0;
            string[] table = bits ? BitRateUnits : ByteRateUnits;

            double scaled;
            int index = Scale(raw, step, table, out scaled);

            int decimals;
            if (index == 0)
            {
                decimals = 0;
            }
            else if (scaled < 10.0)
            {
                decimals = 2;
            }
            else if (scaled < 100.0)
            {
                decimals = 1;
            }
            else
            {
                decimals = 0;
            }

            return Settings.Num(scaled, decimals) + " " + table[index];
        }

        /// <summary>Cumulative volume, always in bytes, e.g. "1.24 GB".</summary>
        public static string Total(double bytes)
        {
            double scaled;
            int index = Scale(bytes, 1024.0, TotalUnits, out scaled);
            int decimals = index == 0 ? 0 : (scaled < 10.0 ? 2 : (scaled < 100.0 ? 1 : 0));
            return Settings.Num(scaled, decimals) + " " + TotalUnits[index];
        }

        public static string Ellipsize(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
            {
                return text;
            }

            if (maxChars <= 1)
            {
                return text.Substring(0, 1);
            }

            return text.Substring(0, maxChars - 1) + "\u2026";
        }

        public static string Duration(TimeSpan span)
        {
            if (span.TotalHours >= 1.0)
            {
                return ((int)span.TotalHours) + "h " + span.Minutes + "m";
            }

            if (span.TotalMinutes >= 1.0)
            {
                return span.Minutes + "m " + span.Seconds + "s";
            }

            return span.Seconds + "s";
        }
    }
}
