using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.NetworkInformation;

namespace InternetSpeedMeter
{
    internal sealed class AdapterInfo
    {
        public string Id;
        public string Name;
        public string Description;

        public string Label
        {
            get
            {
                if (string.IsNullOrEmpty(Description) || Description == Name)
                {
                    return Name;
                }

                return Name + "  —  " + Fmt.Ellipsize(Description, 34);
            }
        }
    }

    /// <summary>One polling result: rates plus the raw byte deltas for the sampled window.</summary>
    internal sealed class Reading
    {
        public double DownBytesPerSecond;
        public double UpBytesPerSecond;
        public long DownBytes;
        public long UpBytes;
        public string SourceName = "";
        public bool Connected;

        /// <summary>
        /// How long the window actually was. Nominally one second, but a late tick or a resume
        /// from sleep stretches it, and the recorder needs to know: the rate stays honest because
        /// it is divided by this, while the byte delta covers the whole stretched window and must
        /// not be filed against a single second.
        /// </summary>
        public double ElapsedSeconds = 1.0;

        /// <summary>
        /// Stable identity of whatever was metered — an adapter id, <see cref="Settings.AdapterAll"/>
        /// for the summed mode, or null when nothing was. <see cref="SourceName"/> is a label and
        /// changes when an adapter is renamed; this does not, so history keys on it.
        /// </summary>
        public string SourceId;
    }

    /// <summary>
    /// Polls per-interface byte counters through System.Net.NetworkInformation, which sits on top
    /// of the IP Helper API. Deltas are divided by the measured elapsed time rather than by the
    /// nominal timer interval, so a late tick (or a machine waking from sleep) cannot inflate the
    /// reported speed.
    /// </summary>
    internal sealed class NetMonitor
    {
        private sealed class Counter
        {
            public long Received;
            public long Sent;
            public double Activity;
        }

        private readonly Dictionary<string, Counter> _counters = new Dictionary<string, Counter>(StringComparer.Ordinal);
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _lastSampleSeconds;
        private bool _primed;
        private string _autoPick;

        /// <summary>Physical, connected interfaces worth metering.</summary>
        public List<AdapterInfo> ListAdapters()
        {
            List<AdapterInfo> list = new List<AdapterInfo>();
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (!IsCandidate(nic))
                    {
                        continue;
                    }

                    AdapterInfo info = new AdapterInfo();
                    info.Id = nic.Id;
                    info.Name = nic.Name;
                    info.Description = nic.Description;
                    list.Add(info);
                }
            }
            catch (NetworkInformationException)
            {
                // Transient IP Helper failure: report whatever was collected.
            }

            return list;
        }

        private static bool IsCandidate(NetworkInterface nic)
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
            {
                return false;
            }

            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                return false;
            }

            string description = nic.Description ?? string.Empty;
            if (description.IndexOf("Pseudo-Interface", StringComparison.OrdinalIgnoreCase) >= 0 ||
                description.IndexOf("Loopback", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Samples every candidate adapter and returns the traffic for the selection described by
        /// <paramref name="adapterMode"/>: "auto", "all", or a specific adapter id.
        /// </summary>
        public Reading Sample(string adapterMode)
        {
            double now = _clock.Elapsed.TotalSeconds;
            double elapsed = now - _lastSampleSeconds;
            _lastSampleSeconds = now;
            if (elapsed < 0.05)
            {
                elapsed = 0.05;
            }

            Reading reading = new Reading();

            NetworkInterface[] nics;
            try
            {
                nics = NetworkInterface.GetAllNetworkInterfaces();
            }
            catch (NetworkInformationException)
            {
                reading.SourceName = "No active adapter";
                return reading;
            }

            Dictionary<string, long[]> deltas = new Dictionary<string, long[]>(StringComparer.Ordinal);
            Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.Ordinal);
            string bestId = null;
            double bestActivity = -1.0;

            foreach (NetworkInterface nic in nics)
            {
                if (!IsCandidate(nic))
                {
                    continue;
                }

                long received;
                long sent;
                try
                {
                    IPInterfaceStatistics stats = nic.GetIPStatistics();
                    received = stats.BytesReceived;
                    sent = stats.BytesSent;
                }
                catch (NetworkInformationException)
                {
                    continue;
                }
                catch (PlatformNotSupportedException)
                {
                    continue;
                }

                Counter counter;
                bool isNew = !_counters.TryGetValue(nic.Id, out counter);
                if (isNew)
                {
                    counter = new Counter();
                    _counters[nic.Id] = counter;
                }

                // A counter going backwards means the adapter or its driver reset; skip that
                // window instead of reporting a nonsense spike.
                bool usable = _primed && !isNew;
                long down = (usable && received >= counter.Received) ? received - counter.Received : 0;
                long up = (usable && sent >= counter.Sent) ? sent - counter.Sent : 0;

                counter.Received = received;
                counter.Sent = sent;

                // Smoothed activity drives "auto": the busiest adapter wins, which keeps idle VPN
                // and virtual adapters from stealing the display.
                counter.Activity = (counter.Activity * 0.7) + ((down + up) * 0.3);
                if (counter.Activity > bestActivity)
                {
                    bestActivity = counter.Activity;
                    bestId = nic.Id;
                }

                deltas[nic.Id] = new long[] { down, up };
                names[nic.Id] = nic.Name;
            }

            ForgetMissingAdapters(deltas);
            _primed = true;

            long selectedDown = 0;
            long selectedUp = 0;
            string selectedName = null;
            string selectedId = null;

            if (adapterMode == Settings.AdapterAll)
            {
                foreach (KeyValuePair<string, long[]> pair in deltas)
                {
                    selectedDown += pair.Value[0];
                    selectedUp += pair.Value[1];
                }

                selectedName = deltas.Count > 0 ? "All adapters (" + deltas.Count + ")" : null;
                selectedId = selectedName != null ? Settings.AdapterAll : null;
            }
            else
            {
                string wanted;
                if (adapterMode == Settings.AdapterAuto)
                {
                    // Hold the previous pick while every adapter is idle so the label stops flickering.
                    if (bestActivity > 0.0 && bestId != null)
                    {
                        _autoPick = bestId;
                    }
                    else if (_autoPick == null || !deltas.ContainsKey(_autoPick))
                    {
                        _autoPick = bestId;
                    }

                    wanted = _autoPick;
                }
                else
                {
                    wanted = adapterMode;
                }

                long[] delta;
                if (wanted != null && deltas.TryGetValue(wanted, out delta))
                {
                    selectedDown = delta[0];
                    selectedUp = delta[1];
                    selectedName = names[wanted];
                    selectedId = wanted;
                }
            }

            reading.DownBytes = selectedDown;
            reading.UpBytes = selectedUp;
            reading.DownBytesPerSecond = selectedDown / elapsed;
            reading.UpBytesPerSecond = selectedUp / elapsed;
            reading.ElapsedSeconds = elapsed;
            reading.Connected = selectedName != null;
            reading.SourceName = selectedName ?? "No active adapter";
            reading.SourceId = selectedId;
            return reading;
        }

        private void ForgetMissingAdapters(Dictionary<string, long[]> seen)
        {
            List<string> gone = null;
            foreach (KeyValuePair<string, Counter> pair in _counters)
            {
                if (!seen.ContainsKey(pair.Key))
                {
                    if (gone == null)
                    {
                        gone = new List<string>();
                    }

                    gone.Add(pair.Key);
                }
            }

            if (gone == null)
            {
                return;
            }

            foreach (string id in gone)
            {
                _counters.Remove(id);
            }
        }
    }
}
