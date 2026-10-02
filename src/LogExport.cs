using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace InternetSpeedMeter
{
    /// <summary>
    /// Reads the binary logs back out as CSV.
    ///
    /// This exists so the recording is useful on its own. The dashboard is optional and needs a
    /// Node install; a fixed-width binary file that only one unwritten program can read would make
    /// the history hostage to that program ever being built. A spreadsheet can open this.
    ///
    /// Rows are streamed rather than collected: a year of minutes is over half a million of them,
    /// and the export must not need them all in memory at once.
    /// </summary>
    internal static class LogExport
    {
        /// <summary>Which of the two logs to read.</summary>
        internal enum Source
        {
            Minutes,
            Raw
        }

        /// <summary>
        /// Writes the records falling in <paramref name="from"/>..<paramref name="to"/> inclusive,
        /// as local dates. Returns the number of data rows written.
        /// </summary>
        public static int Write(Settings settings, Source source, DateTime from, DateTime to, TextWriter writer)
        {
            string folder = source == Source.Raw ? settings.RawFolder : settings.LogFolder;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                throw new DirectoryNotFoundException(
                    (source == Source.Raw ? "Raw folder" : "Log folder") + " not found: " +
                    (string.IsNullOrEmpty(folder) ? "(not set)" : folder));
            }

            Dictionary<byte, string> names = AdapterNames(settings.LogFolder);

            if (source == Source.Raw)
            {
                writer.WriteLine("local_time,utc_time,down_bytes,up_bytes,elapsed_ms,adapter,connected,gap,adapter_changed");
            }
            else
            {
                writer.WriteLine("local_time,utc_time,down_bytes,up_bytes,max_down_bps,max_up_bps,samples,active_samples,adapter,connected,gap,adapter_changed,meter_started");
            }

            int stride = source == Source.Raw ? Recorder.RawRecordBytes : Recorder.MinuteRecordBytes;
            int rows = 0;

            foreach (string path in FilesInRange(folder, source, from, to))
            {
                byte[] bytes = ReadShared(path);
                int count = bytes.Length / stride;
                if (count == 0)
                {
                    continue;
                }

                // The writer appends in real time, so records are normally in order — but a
                // backwards clock step can break that, and a CSV that jumps back in time halfway
                // down is worse than one that took a moment to sort.
                long[] keys = new long[count];
                int[] order = new int[count];
                for (int i = 0; i < count; i++)
                {
                    keys[i] = (long)ReadU32(bytes, i * stride);
                    order[i] = i;
                }

                Array.Sort(keys, order);

                for (int i = 0; i < count; i++)
                {
                    int at = order[i] * stride;
                    long unitsSinceEpoch = keys[i];
                    DateTime utc = source == Source.Raw
                        ? Recorder.Epoch.AddSeconds(unitsSinceEpoch)
                        : Recorder.Epoch.AddMinutes(unitsSinceEpoch);
                    DateTime local = utc.ToLocalTime();

                    if (local.Date < from.Date || local.Date > to.Date)
                    {
                        continue;
                    }

                    writer.WriteLine(source == Source.Raw
                        ? RawRow(bytes, at, local, utc, names)
                        : MinuteRow(bytes, at, local, utc, names));
                    rows++;
                }
            }

            return rows;
        }

        private static string RawRow(byte[] b, int at, DateTime local, DateTime utc, Dictionary<byte, string> names)
        {
            int flags = b[at + 15];
            return Stamp(local) + "," + Stamp(utc) + "," +
                   ReadU32(b, at + 4).ToString(CultureInfo.InvariantCulture) + "," +
                   ReadU32(b, at + 8).ToString(CultureInfo.InvariantCulture) + "," +
                   ReadU16(b, at + 12).ToString(CultureInfo.InvariantCulture) + "," +
                   Csv(AdapterName(names, b[at + 14])) + "," +
                   Flags(flags);
        }

        private static string MinuteRow(byte[] b, int at, DateTime local, DateTime utc, Dictionary<byte, string> names)
        {
            int flags = b[at + 33];
            return Stamp(local) + "," + Stamp(utc) + "," +
                   ReadU64(b, at + 4).ToString(CultureInfo.InvariantCulture) + "," +
                   ReadU64(b, at + 12).ToString(CultureInfo.InvariantCulture) + "," +
                   ReadU32(b, at + 20).ToString(CultureInfo.InvariantCulture) + "," +
                   ReadU32(b, at + 24).ToString(CultureInfo.InvariantCulture) + "," +
                   ReadU16(b, at + 28).ToString(CultureInfo.InvariantCulture) + "," +
                   ReadU16(b, at + 30).ToString(CultureInfo.InvariantCulture) + "," +
                   Csv(AdapterName(names, b[at + 32])) + "," +
                   Flags(flags) + "," +
                   ((flags & Recorder.FlagMeterStarted) != 0 ? "1" : "0");
        }

        /// <summary>
        /// Flags as three columns rather than one bitfield. The point of the CSV is that a
        /// spreadsheet can use it, and nothing in a spreadsheet wants to mask bits.
        /// </summary>
        private static string Flags(int flags)
        {
            return ((flags & Recorder.FlagConnected) != 0 ? "1," : "0,") +
                   ((flags & Recorder.FlagGap) != 0 ? "1," : "0,") +
                   ((flags & Recorder.FlagAdapterChanged) != 0 ? "1" : "0");
        }

        private static string Stamp(DateTime value)
        {
            return value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        private static Dictionary<byte, string> AdapterNames(string minuteFolder)
        {
            Dictionary<byte, string> names = new Dictionary<byte, string>();
            try
            {
                foreach (AdapterRow row in Recorder.ReadAdapterTable(minuteFolder))
                {
                    // A name is nicer, but the id is what makes the row identifiable if a renamed
                    // adapter left two entries behind.
                    names[row.Index] = string.IsNullOrEmpty(row.Name) ? row.Id : row.Name;
                }
            }
            catch (Exception)
            {
                // Without the table the export still carries every number; only the labels degrade
                // to their indices, and that is not worth failing an export over.
            }

            return names;
        }

        private static string AdapterName(Dictionary<byte, string> names, byte index)
        {
            if (index == Recorder.AdapterNone)
            {
                return "none";
            }

            string name;
            return names.TryGetValue(index, out name) ? name : "#" + index.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The files whose name-date overlaps the range, in chronological order.
        ///
        /// File names are local dates, so a month file is included whenever the range touches it at
        /// all and the per-record filter does the exact trimming.
        /// </summary>
        private static List<string> FilesInRange(string folder, Source source, DateTime from, DateTime to)
        {
            string format = source == Source.Raw ? "yyyy-MM-dd" : "yyyy-MM";
            List<string> keep = new List<string>();
            List<DateTime> stamps = new List<DateTime>();

            foreach (string path in Directory.GetFiles(folder, "*.bin"))
            {
                DateTime stamp;
                if (!DateTime.TryParseExact(
                        Path.GetFileNameWithoutExtension(path), format,
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out stamp))
                {
                    continue;
                }

                DateTime last = source == Source.Raw ? stamp : stamp.AddMonths(1).AddDays(-1);
                if (last < from.Date || stamp > to.Date)
                {
                    continue;
                }

                keep.Add(path);
                stamps.Add(stamp);
            }

            DateTime[] keys = stamps.ToArray();
            string[] paths = keep.ToArray();
            Array.Sort(keys, paths);
            return new List<string>(paths);
        }

        /// <summary>
        /// Reads a file the running meter may still be appending to, which is the normal case.
        /// A trailing partial record is dropped by the caller's integer division.
        /// </summary>
        private static byte[] ReadShared(string path)
        {
            using (FileStream stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] bytes = new byte[stream.Length];
                int read = 0;
                while (read < bytes.Length)
                {
                    int chunk = stream.Read(bytes, read, bytes.Length - read);
                    if (chunk <= 0)
                    {
                        break;
                    }

                    read += chunk;
                }

                if (read == bytes.Length)
                {
                    return bytes;
                }

                byte[] trimmed = new byte[read];
                Array.Copy(bytes, trimmed, read);
                return trimmed;
            }
        }

        private static string Csv(string value)
        {
            if (value.IndexOf(',') < 0 && value.IndexOf('"') < 0)
            {
                return value;
            }

            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static ushort ReadU16(byte[] b, int at)
        {
            return (ushort)(b[at] | (b[at + 1] << 8));
        }

        private static uint ReadU32(byte[] b, int at)
        {
            return (uint)b[at] | ((uint)b[at + 1] << 8) | ((uint)b[at + 2] << 16) | ((uint)b[at + 3] << 24);
        }

        private static ulong ReadU64(byte[] b, int at)
        {
            return ReadU32(b, at) | ((ulong)ReadU32(b, at + 4) << 32);
        }
    }
}
