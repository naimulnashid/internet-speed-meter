using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace InternetSpeedMeter
{
    /// <summary>One row of adapters.tsv: the byte stored in every record, and what it stands for.</summary>
    internal sealed class AdapterRow
    {
        public byte Index;
        public string Id;
        public string Name;
    }

    /// <summary>
    /// Persists the sampling loop to disk, so speed history outlives the process.
    ///
    /// Two files, because the two halves of the data are worth different amounts. Raw one-second
    /// samples are 1.38 MB a day and are only interesting while recent — they carry burst shape
    /// and sub-minute stalls — so they are pruned after a fortnight and live outside the synced
    /// folder. Minute rollups are 57 KB a day, carry the max of the seconds inside them, and are
    /// kept forever.
    ///
    /// Carrying the max is what makes the small file sufficient: a peak query over a year never
    /// needs the seconds it was averaged from. And because this class writes the rollup itself
    /// rather than leaving it to a collector, the history does not depend on anything else being
    /// installed or scheduled — the failure mode where a dashboard stops running and takes months
    /// of history with it cannot happen here.
    ///
    /// Nothing here may throw into the sampling loop. Every I/O failure downgrades what is
    /// recorded and is reported once to error.log; the meter itself keeps running.
    /// </summary>
    internal sealed class Recorder : IDisposable
    {
        /// <summary>Bytes per record in the raw file. Fixed width: readers seek, they do not parse.</summary>
        internal const int RawRecordBytes = 16;

        /// <summary>Bytes per record in the minute file. 34 used, padded to 40 for later fields.</summary>
        internal const int MinuteRecordBytes = 40;

        /// <summary>
        /// Windows longer than this are not attributed to a second.
        ///
        /// The Stopwatch behind the reading keeps running across a suspend on some power states,
        /// so a resume can hand us one "sample" spanning an hour. The rate is still honest — it is
        /// divided by the measured elapsed time — but the byte delta covers the whole hour, and
        /// filing that against one second would invent a 500 MB/s peak that never happened. Such a
        /// window is recorded as a flagged gap carrying no bytes instead.
        /// </summary>
        private const double MaxWindowSeconds = 3.0;

        internal const int FlagConnected = 1;
        internal const int FlagGap = 2;
        internal const int FlagAdapterChanged = 4;

        /// <summary>
        /// Minute records only: the meter started partway through this minute, so it holds fewer
        /// than 60 samples for a reason that says nothing about how well it is sampling.
        ///
        /// Without this the two are indistinguishable after the fact. A reader can spot a partial
        /// minute at the edge of a recording gap, but not one where the meter stopped and started
        /// again inside a single minute — both neighbours are present and the count is simply low.
        /// That is exactly what a restart looks like, and reading it as dropped ticks is how a
        /// healthy meter gets reported as faulty.
        /// </summary>
        internal const int FlagMeterStarted = 8;

        /// <summary>Index 0 is reserved for "nothing was being metered".</summary>
        internal const byte AdapterNone = 0;

        internal static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>Name of the adapter table, which lives beside the minute files.</summary>
        internal const string AdapterTableName = "adapters.tsv";

        private readonly string _minuteFolder;
        private readonly string _rawFolder;
        private readonly long _activeThresholdBps;
        private readonly int _rawRetentionDays;

        private bool _enabled;
        private bool _rawEnabled;
        private bool _faultReported;

        // One flush per minute, so the buffer never needs to hold more than the samples of a single
        // minute. The cap is generous against timer drift; filling it early just flushes early.
        private readonly byte[] _rawBuffer = new byte[128 * RawRecordBytes];
        private int _rawBufferBytes;

        // The minute being accumulated. -1 until the first sample arrives.
        private long _minuteIndex = -1;
        private ulong _minuteDown;
        private ulong _minuteUp;
        private uint _minuteMaxDown;
        private uint _minuteMaxUp;
        private int _minuteSamples;
        private int _minuteActiveSamples;
        private int _minuteFlags;
        private byte _minuteAdapter;

        private readonly Dictionary<string, byte> _adapterIndex = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private byte _nextAdapterIndex = 1;
        private byte _lastAdapter = AdapterNone;
        private string _prunedForDate;
        private bool _firstMinuteWritten;

        public Recorder(Settings settings)
        {
            _activeThresholdBps = settings.ActiveThresholdBps;
            _rawRetentionDays = settings.RawRetentionDays;

            _minuteFolder = (settings.LogFolder ?? string.Empty).Trim();
            _rawFolder = (settings.RawFolder ?? string.Empty).Trim();

            _enabled = settings.Record && _minuteFolder.Length > 0 && EnsureFolder(_minuteFolder);

            // The two halves fail independently, and deliberately not symmetrically. Losing the raw
            // file costs burst detail for one session and repairs itself; losing the rollup costs
            // history permanently. So raw absent is fine, and an empty RawFolder is the supported
            // way to ask for minutes only — but raw with nowhere to graduate into is 1.38 MB a day
            // of writes buying nothing, so it is not recorded alone.
            _rawEnabled = _enabled && _rawFolder.Length > 0 && EnsureFolder(_rawFolder);

            if (_enabled)
            {
                LoadAdapterTable();
                PruneRawFiles();
            }
        }

        /// <summary>False when nothing is being written, for callers that want to say so.</summary>
        public bool Enabled
        {
            get { return _enabled; }
        }

        /// <summary>Files a reading. Called once per tick from the sampling loop.</summary>
        public void Add(Reading reading)
        {
            if (!_enabled || reading == null)
            {
                return;
            }

            try
            {
                DateTime nowUtc = DateTime.UtcNow;
                long seconds = (long)(nowUtc - Epoch).TotalSeconds;
                long minute = seconds / 60;

                if (_minuteIndex >= 0 && minute != _minuteIndex)
                {
                    Flush();

                    // The flush is the one place a write failure can disable recording, and it must
                    // not then accumulate a minute that will never be written.
                    if (!_enabled)
                    {
                        return;
                    }
                }

                if (_minuteIndex < 0)
                {
                    _minuteIndex = minute;
                    _minuteAdapter = AdapterNone;
                }

                byte adapter = IndexForAdapter(reading.SourceId, reading.SourceName);
                bool gap = reading.ElapsedSeconds > MaxWindowSeconds;

                int flags = 0;
                if (reading.Connected)
                {
                    flags |= FlagConnected;
                }

                if (gap)
                {
                    flags |= FlagGap;
                }

                // An adapter change matters more than it looks: "auto" follows the busiest adapter,
                // so without this the history silently splices Wi-Fi and Ethernet into one series.
                if (adapter != _lastAdapter)
                {
                    flags |= FlagAdapterChanged;
                    if (_minuteSamples > 0)
                    {
                        _minuteFlags |= FlagAdapterChanged;
                    }
                }

                _lastAdapter = adapter;

                uint down = gap ? 0 : ToUInt32(reading.DownBytes);
                uint up = gap ? 0 : ToUInt32(reading.UpBytes);

                WriteRaw(seconds, down, up, reading.ElapsedSeconds, adapter, flags);

                _minuteSamples++;
                _minuteAdapter = adapter;
                _minuteFlags |= flags & (FlagConnected | FlagGap);

                if (!gap)
                {
                    _minuteDown += down;
                    _minuteUp += up;

                    uint downRate = ToUInt32(reading.DownBytesPerSecond);
                    uint upRate = ToUInt32(reading.UpBytesPerSecond);
                    if (downRate > _minuteMaxDown)
                    {
                        _minuteMaxDown = downRate;
                    }

                    if (upRate > _minuteMaxUp)
                    {
                        _minuteMaxUp = upRate;
                    }

                    if (downRate + (double)upRate >= _activeThresholdBps)
                    {
                        _minuteActiveSamples++;
                    }
                }
            }
            catch (Exception ex)
            {
                Fault("recording a sample", ex);
            }
        }

        /// <summary>
        /// Writes the buffered minute out. Called at every minute boundary and once on shutdown, so
        /// a clean exit keeps its partial minute and a crash costs at most the current one.
        /// </summary>
        public void Flush()
        {
            if (!_enabled || _minuteIndex < 0 || _minuteSamples == 0)
            {
                _rawBufferBytes = 0;
                _minuteIndex = -1;
                return;
            }

            WriteMinute(_minuteIndex);
        }

        public void Dispose()
        {
            try
            {
                Flush();
            }
            catch (Exception)
            {
                // Shutdown is not the place to raise anything.
            }
        }

        /// <summary>Writes one minute record plus the raw samples behind it, then clears both.</summary>
        private void WriteMinute(long minute)
        {
            byte[] record = new byte[MinuteRecordBytes];
            WriteU32(record, 0, (uint)minute);
            WriteU64(record, 4, _minuteDown);
            WriteU64(record, 12, _minuteUp);
            WriteU32(record, 20, _minuteMaxDown);
            WriteU32(record, 24, _minuteMaxUp);
            WriteU16(record, 28, (ushort)Math.Min(_minuteSamples, ushort.MaxValue));
            WriteU16(record, 30, (ushort)Math.Min(_minuteActiveSamples, ushort.MaxValue));
            record[32] = _minuteAdapter;

            int flags = _minuteFlags;
            if (!_firstMinuteWritten)
            {
                flags |= FlagMeterStarted;
                _firstMinuteWritten = true;
            }

            record[33] = (byte)flags;
            // 34..39 stay zero: room for fields this format does not have yet.

            DateTime local = Epoch.AddMinutes(minute).ToLocalTime();

            // Every buffered sample belongs to this one minute, and a day boundary is always a
            // minute boundary, so a batch can never straddle two daily files.
            if (_rawBufferBytes > 0)
            {
                Append(
                    Path.Combine(_rawFolder, local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".bin"),
                    _rawBuffer,
                    _rawBufferBytes,
                    false);
            }

            Append(
                Path.Combine(_minuteFolder, local.ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".bin"),
                record,
                record.Length,
                true);

            _rawBufferBytes = 0;
            _minuteIndex = -1;
            _minuteDown = 0;
            _minuteUp = 0;
            _minuteMaxDown = 0;
            _minuteMaxUp = 0;
            _minuteSamples = 0;
            _minuteActiveSamples = 0;
            _minuteFlags = 0;

            PruneRawFiles();
        }

        private void WriteRaw(long seconds, uint down, uint up, double elapsedSeconds, byte adapter, int flags)
        {
            if (!_rawEnabled)
            {
                return;
            }

            if (_rawBufferBytes + RawRecordBytes > _rawBuffer.Length)
            {
                // Only reachable if a minute somehow produced more samples than the cap. Spilling
                // early keeps the write bounded; the records are still in order.
                Append(
                    Path.Combine(
                        _rawFolder,
                        DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".bin"),
                    _rawBuffer,
                    _rawBufferBytes,
                    false);
                _rawBufferBytes = 0;
            }

            double ms = elapsedSeconds * 1000.0;
            ushort elapsedMs = ms >= ushort.MaxValue ? ushort.MaxValue : (ushort)ms;

            int at = _rawBufferBytes;
            WriteU32(_rawBuffer, at, (uint)seconds);
            WriteU32(_rawBuffer, at + 4, down);
            WriteU32(_rawBuffer, at + 8, up);
            WriteU16(_rawBuffer, at + 12, elapsedMs);
            _rawBuffer[at + 14] = adapter;
            _rawBuffer[at + 15] = (byte)flags;
            _rawBufferBytes += RawRecordBytes;
        }

        /// <summary>
        /// Appends bytes, sharing the file for reading so the dashboard can follow a file that is
        /// still being written. A failure on the raw file only disables raw; a failure on the
        /// minute file stops recording, since the point of the exercise is gone.
        /// </summary>
        private void Append(string path, byte[] buffer, int count, bool essential)
        {
            try
            {
                using (FileStream stream = new FileStream(
                    path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.None))
                {
                    stream.Write(buffer, 0, count);
                }
            }
            catch (Exception ex)
            {
                if (essential)
                {
                    _enabled = false;
                    _rawEnabled = false;
                    Fault("writing " + path + " — recording stopped for this session", ex);
                }
                else
                {
                    _rawEnabled = false;
                    Fault("writing " + path + " — raw samples disabled, minute history continues", ex);
                }
            }
        }

        /// <summary>
        /// Maps an adapter id to the byte stored on every record, learning ids as they appear.
        ///
        /// The table lives beside the minute files rather than the raw ones: both record types
        /// reference it, and a fortnightly prune of the raw folder must never be able to orphan the
        /// names of a history that goes back years.
        /// </summary>
        private byte IndexForAdapter(string id, string name)
        {
            if (string.IsNullOrEmpty(id))
            {
                return AdapterNone;
            }

            byte index;
            if (_adapterIndex.TryGetValue(id, out index))
            {
                return index;
            }

            if (_nextAdapterIndex == AdapterNone)
            {
                // 255 distinct adapters on one machine is not a real scenario, but wrapping the
                // index would silently relabel history, so unknown is the safer answer.
                return AdapterNone;
            }

            index = _nextAdapterIndex;
            _adapterIndex[id] = index;
            _nextAdapterIndex = (byte)(_nextAdapterIndex == byte.MaxValue ? AdapterNone : _nextAdapterIndex + 1);

            AppendAdapterRow(index, id, name);
            return index;
        }

        private string AdapterTablePath
        {
            get { return Path.Combine(_minuteFolder, AdapterTableName); }
        }

        /// <summary>
        /// Parses adapters.tsv. Shared with the CSV exporter, which needs the same file read the
        /// other way round — one parser, so the two can never disagree about what an index means.
        /// Throws on I/O failure; what to do about that differs by caller.
        /// </summary>
        internal static List<AdapterRow> ReadAdapterTable(string folder)
        {
            List<AdapterRow> rows = new List<AdapterRow>();
            string path = Path.Combine(folder, AdapterTableName);
            if (!File.Exists(path))
            {
                return rows;
            }

            foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                string[] parts = line.Split('\t');
                if (parts.Length < 2)
                {
                    continue;
                }

                byte index;
                if (!byte.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out index) ||
                    index == AdapterNone)
                {
                    continue;
                }

                AdapterRow row = new AdapterRow();
                row.Index = index;
                row.Id = parts[1];
                row.Name = parts.Length > 2 ? parts[2] : string.Empty;
                rows.Add(row);
            }

            return rows;
        }

        private void LoadAdapterTable()
        {
            try
            {
                foreach (AdapterRow row in ReadAdapterTable(_minuteFolder))
                {
                    _adapterIndex[row.Id] = row.Index;
                    if (row.Index >= _nextAdapterIndex)
                    {
                        _nextAdapterIndex = (byte)(row.Index == byte.MaxValue ? AdapterNone : row.Index + 1);
                    }
                }
            }
            catch (Exception ex)
            {
                // Starting from an empty table would hand out indices already in use and corrupt the
                // meaning of existing history, so recording stops instead.
                _enabled = false;
                _rawEnabled = false;
                Fault("reading " + AdapterTablePath, ex);
            }
        }

        private void AppendAdapterRow(byte index, string id, string name)
        {
            try
            {
                bool fresh = !File.Exists(AdapterTablePath);

                // Encoding stated rather than defaulted: adapter names carry whatever the user
                // renamed them to, and this file is parsed by the dashboard as well as by us.
                // UTF-8 without a BOM, to match what the reader below assumes.
                using (StreamWriter writer = new StreamWriter(AdapterTablePath, true, new UTF8Encoding(false)))
                {
                    if (fresh)
                    {
                        writer.WriteLine("# index\tid\tname — referenced by adapterIx in the .bin records");
                    }

                    writer.WriteLine(
                        index.ToString(CultureInfo.InvariantCulture) + "\t" + id + "\t" +
                        (name ?? string.Empty).Replace('\t', ' '));
                }
            }
            catch (Exception ex)
            {
                // The records still carry the index; only the human-readable name is lost, and it is
                // relearned the next time this adapter appears with a writable table.
                _adapterIndex.Remove(id);
                Fault("writing " + AdapterTablePath, ex);
            }
        }

        /// <summary>
        /// Deletes raw files past the retention window, at most once per local day.
        ///
        /// Only files whose name parses as the exact date format are touched. A wildcard delete in
        /// a folder a user can repoint at anything is not worth the brevity.
        /// </summary>
        private void PruneRawFiles()
        {
            if (!_rawEnabled || _rawRetentionDays <= 0)
            {
                return;
            }

            string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (_prunedForDate == today)
            {
                return;
            }

            _prunedForDate = today;

            try
            {
                DateTime cutoff = DateTime.Now.Date.AddDays(-_rawRetentionDays);
                foreach (string path in Directory.GetFiles(_rawFolder, "*.bin"))
                {
                    string name = Path.GetFileNameWithoutExtension(path);
                    DateTime stamp;
                    if (!DateTime.TryParseExact(
                            name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out stamp))
                    {
                        continue;
                    }

                    if (stamp < cutoff)
                    {
                        File.Delete(path);
                    }
                }
            }
            catch (Exception ex)
            {
                // Disk fills slower than history is worth; a failed prune is not worth stopping for.
                Fault("pruning " + _rawFolder, ex);
            }
        }

        private static bool EnsureFolder(string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Notes the first failure of the session to error.log and then stays quiet. A recorder that
        /// cannot write has usually lost a whole drive, and one line an hour for a week helps nobody.
        /// </summary>
        private void Fault(string what, Exception ex)
        {
            if (_faultReported)
            {
                return;
            }

            _faultReported = true;

            try
            {
                Directory.CreateDirectory(Settings.Folder);
                File.AppendAllText(
                    Path.Combine(Settings.Folder, "error.log"),
                    DateTime.Now.ToString("u") + "  recorder: " + what + Environment.NewLine +
                    ex + Environment.NewLine + Environment.NewLine);
            }
            catch (Exception)
            {
            }
        }

        private static uint ToUInt32(double value)
        {
            if (value <= 0.0)
            {
                return 0;
            }

            return value >= uint.MaxValue ? uint.MaxValue : (uint)value;
        }

        private static uint ToUInt32(long value)
        {
            if (value <= 0L)
            {
                return 0;
            }

            return value >= uint.MaxValue ? uint.MaxValue : (uint)value;
        }

        // Little-endian by hand rather than through BitConverter: the reader on the other side is a
        // DataView in a browser, and the format should say what it is instead of inheriting the
        // byte order of whatever ran the writer.
        private static void WriteU16(byte[] buffer, int at, ushort value)
        {
            buffer[at] = (byte)value;
            buffer[at + 1] = (byte)(value >> 8);
        }

        private static void WriteU32(byte[] buffer, int at, uint value)
        {
            buffer[at] = (byte)value;
            buffer[at + 1] = (byte)(value >> 8);
            buffer[at + 2] = (byte)(value >> 16);
            buffer[at + 3] = (byte)(value >> 24);
        }

        private static void WriteU64(byte[] buffer, int at, ulong value)
        {
            WriteU32(buffer, at, (uint)value);
            WriteU32(buffer, at + 4, (uint)(value >> 32));
        }
    }
}
