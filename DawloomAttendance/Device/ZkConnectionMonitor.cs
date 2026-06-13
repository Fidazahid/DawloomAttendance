using System;
using System.Threading;
using System.Threading.Tasks;

namespace DawloomAttendance.Device
{
    /// <summary>
    /// Keeps a self-healing connection to the device. A single background task:
    ///   1. (re)connects, backing off <c>ReconnectIntervalSeconds</c> between attempts;
    ///   2. once connected, pings every <c>KeepAliveIntervalSeconds</c> (cheapest SDK
    ///      no-op) and drops back to the reconnect loop the moment a ping fails.
    ///
    /// On every successful (re)connect it raises <see cref="ConnectedOrReconnected"/>.
    /// Subscribers run synchronously on the monitor's thread, so device work they do
    /// (e.g. backfill) stays serialized with the keep-alive ping — no concurrent SDK
    /// calls. Live-event re-registration (RegEvent) already happens inside
    /// <see cref="ZkDeviceClient.Connect"/>, which is required after every reconnect.
    /// </summary>
    public sealed class ZkConnectionMonitor : IDisposable
    {
        private readonly IZkDevice _device;
        private readonly object _gate = new object();

        private DeviceSettings _settings;
        private CancellationTokenSource _cts;
        private Task _loop;

        public ZkConnectionMonitor(IZkDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
        }

        /// <summary>Fired on the monitor's background thread after each successful (re)connect.</summary>
        public event Action ConnectedOrReconnected;

        /// <summary>Diagnostic messages: (level, event, detail).</summary>
        public event Action<string, string, string> Log;

        public bool IsRunning { get; private set; }

        public void Start(DeviceSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            Stop();

            lock (_gate)
            {
                _settings = settings;
                _cts = new CancellationTokenSource();
                IsRunning = true;
                _loop = Task.Run(() => RunAsync(_cts.Token));
            }
        }

        public void Stop()
        {
            CancellationTokenSource cts;
            Task loop;
            lock (_gate)
            {
                cts = _cts; loop = _loop;
                _cts = null; _loop = null;
                IsRunning = false;
            }

            if (cts != null)
            {
                cts.Cancel();
                // Don't block forever if a long device read is in flight.
                try { loop?.Wait(TimeSpan.FromSeconds(5)); } catch { /* aggregate/cancel */ }
                cts.Dispose();
            }

            try { _device.Disconnect(); } catch { /* best effort */ }
        }

        private async Task RunAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                bool connected = false;
                try
                {
                    connected = _device.Connect(_settings);
                }
                catch (Exception ex)
                {
                    Log?.Invoke("Error", "ConnectThrew", ex.Message);
                }

                if (!connected)
                {
                    string reason = string.IsNullOrWhiteSpace(_device.LastMessage) ? "connect failed" : _device.LastMessage;
                    Log?.Invoke("Error", "ConnectFailed",
                        $"{reason} (retrying in {_settings.ReconnectIntervalSeconds}s)");
                    await DelaySeconds(_settings.ReconnectIntervalSeconds, ct).ConfigureAwait(false);
                    continue;
                }

                Log?.Invoke("Info", "Connected", "device connection established");

                // Backfill / live-feed refresh hooks run here, on this thread.
                try { ConnectedOrReconnected?.Invoke(); }
                catch (Exception ex) { Log?.Invoke("Warn", "PostConnectHandler", ex.Message); }

                // Keep-alive loop.
                while (!ct.IsCancellationRequested)
                {
                    await DelaySeconds(_settings.KeepAliveIntervalSeconds, ct).ConfigureAwait(false);
                    if (ct.IsCancellationRequested) break;

                    bool alive;
                    try { alive = _device.Ping(); }
                    catch { alive = false; }

                    if (!alive)
                    {
                        Log?.Invoke("Warn", "KeepAliveFailed", "ping failed — reconnecting");
                        try { _device.Disconnect(); } catch { /* best effort */ }
                        break; // fall back to the outer reconnect loop
                    }
                }
            }
        }

        private static async Task DelaySeconds(int seconds, CancellationToken ct)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, seconds)), ct).ConfigureAwait(false); }
            catch (TaskCanceledException) { /* stopping */ }
        }

        public void Dispose() => Stop();
    }
}
