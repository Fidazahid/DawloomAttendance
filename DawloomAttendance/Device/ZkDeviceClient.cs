using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using DawloomAttendance.Device.Events;

namespace DawloomAttendance.Device
{
    /// <summary>
    /// Wraps the ZKTeco ZKEMKeeper SDK.
    ///
    /// Threading: the SDK is a 32-bit STA ActiveX component, and its real-time events
    /// (OnAttTransactionEx) are delivered through a COM connection point — which only
    /// works if the thread that created the object and called RegEvent is an STA thread
    /// that pumps Windows messages. So ALL COM work runs on one dedicated STA thread
    /// with a <see cref="Dispatcher"/> message loop; public methods marshal onto it via
    /// Dispatcher.Invoke (which also serializes access — never two calls at once).
    ///
    /// Data access uses the strongly-typed interop (ZkSdk=true). Late-bound reflection
    /// is kept only as a build-without-SDK fallback; it can connect/ping but cannot
    /// marshal the SDK's out/ref record fields, so ReadAllLogs needs the typed build.
    /// </summary>
    public sealed class ZkDeviceClient : IZkDevice
    {
        // The SDK registers the CZKEM coclass under these ProgIDs. We try each.
        private static readonly string[] ProgIds = { "zkemkeeper.ZKEM", "zkemkeeper.ZKEM.1" };

        private object _device;          // the live COM CZKEM instance (lives on _comThread)
        private Type _type;              // its runtime (COM) type
#if ZK_TYPED
        private zkemkeeper.CZKEM _typed; // strongly-typed view of _device, for event wiring
#endif
        private int _machineNumber = 1;  // remembered from Connect, used by Ping
        private DeviceConnectionState _state = DeviceConnectionState.Disconnected;

        // Dedicated STA thread that owns the COM object and pumps messages for events.
        private readonly object _threadGate = new object();
        private Thread _comThread;
        private Dispatcher _dispatcher;

        /// <summary>Human-readable detail of the last operation, for the UI/log.</summary>
        public string LastMessage { get; private set; } = string.Empty;

        public DeviceConnectionState State
        {
            get => _state;
            private set
            {
                if (_state == value) return;
                _state = value;
                StateChanged?.Invoke(value);
            }
        }

        public event Action<DeviceConnectionState> StateChanged;

        /// <summary>Raised for every live punch (OnAttTransactionEx), on the COM thread.</summary>
        public event Action<PunchEvent> PunchReceived;

        /// <summary>True if the ZKEMKeeper COM component is registered on this machine.</summary>
        public bool SdkAvailable => ResolveType() != null;

        // ---- Public API: marshal onto the STA COM thread -----------------------------

        public bool Connect(DeviceSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            return Run(() => ConnectCore(settings));
        }

        public void Disconnect() => Run(DisconnectCore);

        public bool Ping() => Run(PingCore);

        public IEnumerable<PunchEvent> ReadAllLogs(int machineNumber) => Run(() => ReadAllLogsCore(machineNumber));

        public IEnumerable<DeviceUser> ReadAllUsers(int machineNumber) => Run(() => ReadAllUsersCore(machineNumber));

        public bool PushUser(int machineNumber, DeviceUser user) => Run(() => PushUserCore(machineNumber, user));

        public bool DeleteUser(int machineNumber, string enrollNumber) => Run(() => DeleteUserCore(machineNumber, enrollNumber));

        public bool SyncTime(int machineNumber) => Run(() => SyncTimeCore(machineNumber));

        public DateTime? GetDeviceTime(int machineNumber) => Run(() => GetDeviceTimeCore(machineNumber));

        public int GetLastError() => Run(GetLastErrorCore);

        public void Dispose()
        {
            try { Run(DisconnectCore); } catch { /* best effort */ }
            var d = _dispatcher;
            if (d != null)
            {
                try { d.InvokeShutdown(); } catch { }
                try { _comThread?.Join(2000); } catch { }
                _dispatcher = null;
                _comThread = null;
            }
        }

        // ---- Core implementations: all run ON the STA COM thread ---------------------

        private bool ConnectCore(DeviceSettings settings)
        {
            State = DeviceConnectionState.Connecting;
            _machineNumber = settings.MachineNumber;

            var type = ResolveType();
            if (type == null)
            {
                LastMessage =
                    "ZKTeco SDK (zkemkeeper) is not registered on this PC. " +
                    "Drop the SDK DLLs into lib\\zkteco and run Register.bat as Administrator.";
                State = DeviceConnectionState.Error;
                return false;
            }

            try
            {
#if ZK_TYPED
                _typed = new zkemkeeper.CZKEMClass();
                _device = _typed;
                _type = _typed.GetType();
#else
                _device = Activator.CreateInstance(type);
                _type = type;
#endif
                Invoke("SetCommPassword", settings.CommKey);

                bool ok = Convert.ToBoolean(Invoke("Connect_Net", settings.Ip, settings.Port));
                if (!ok)
                {
                    int err = GetLastErrorCore();
                    LastMessage = $"Connect_Net failed (SDK error code {err}). " +
                                  "Check IP, port, comm password, and that port 4370 is reachable.";
                    State = DeviceConnectionState.Error;
                    ReleaseDevice();
                    return false;
                }

                LastMessage = "Connected to device.";
                State = DeviceConnectionState.Connected;
#if ZK_TYPED
                WireLiveCapture(settings);
#endif
                if (settings.SyncTimeOnConnect && SyncTimeCore(settings.MachineNumber))
                    LastMessage += " Clock synced to PC.";

                return true;
            }
            catch (Exception ex)
            {
                LastMessage = "Connect threw: " + ex.Message;
                State = DeviceConnectionState.Error;
                ReleaseDevice();
                return false;
            }
        }

        private void DisconnectCore()
        {
            if (_device != null)
            {
#if ZK_TYPED
                UnwireLiveCapture();
#endif
                try { Invoke("Disconnect"); }
                catch { /* best effort */ }
                ReleaseDevice();
                LastMessage = "Disconnected.";
            }
            State = DeviceConnectionState.Disconnected;
        }

        /// <summary>Cheapest no-op to confirm the link is alive (reads the serial number).</summary>
        private bool PingCore()
        {
            if (_device == null) return false;
            try
            {
                var args = new object[] { _machineNumber, string.Empty };
                var result = _type.InvokeMember(
                    "GetSerialNumber", BindingFlags.InvokeMethod, null, _device, args);
                return result == null || Convert.ToBoolean(result);
            }
            catch
            {
                return false;
            }
        }

        private IEnumerable<PunchEvent> ReadAllLogsCore(int machineNumber)
        {
            var result = new List<PunchEvent>();
            if (_device == null) return result;

            // Freeze the device during the bulk read so incoming punches don't
            // interfere; always re-enable it in the finally (per SDK guidance).
            try { Invoke("EnableDevice", machineNumber, false); } catch { /* best effort */ }
            try
            {
#if ZK_TYPED
                if (!_typed.ReadGeneralLogData(machineNumber))
                    return result;

                string enroll; int verify, inout, year, month, day, hour, minute, second;
                int workCode = 0;
                while (_typed.SSR_GetGeneralLogData(machineNumber, out enroll, out verify, out inout,
                           out year, out month, out day, out hour, out minute, out second, ref workCode))
                {
                    DateTime ts;
                    try { ts = new DateTime(year, month, day, hour, minute, second); }
                    catch { continue; } // skip a malformed/garbage record rather than abort the read

                    result.Add(new PunchEvent
                    {
                        EnrollNumber = enroll,
                        IsValid      = true,        // stored records are already accepted punches
                        VerifyMethod = verify,      // dwVerifyMode
                        AttState     = inout,       // dwInOutMode (check-in/out)
                        Timestamp    = ts,
                        WorkCode     = workCode
                    });
                }
#endif
                return result;
            }
            finally
            {
                try { Invoke("EnableDevice", machineNumber, true); } catch { /* best effort */ }
            }
        }

        private IEnumerable<DeviceUser> ReadAllUsersCore(int machineNumber)
        {
            var users = new List<DeviceUser>();
            if (_device == null) return users;
#if ZK_TYPED
            if (!_typed.ReadAllUserID(machineNumber))
                return users;

            string enroll, name, password; int privilege; bool enabled;
            while (_typed.SSR_GetAllUserInfo(machineNumber, out enroll, out name, out password, out privilege, out enabled))
            {
                users.Add(new DeviceUser
                {
                    EnrollNumber = enroll,
                    Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
                    Privilege = privilege,
                    Enabled = enabled
                });
            }
#endif
            // Late-bound can't marshal the out params (same reason as log reads), so
            // user import requires the typed build (ZkSdk=true).
            return users;
        }

        private bool PushUserCore(int machineNumber, DeviceUser user)
        {
            if (_device == null || user == null) return false;
#if ZK_TYPED
            try
            {
                _typed.EnableDevice(machineNumber, false);
                try
                {
                    // SSR_SetUserInfo(machine, enroll, name, password, privilege, enabled)
                    bool ok = _typed.SSR_SetUserInfo(machineNumber, user.EnrollNumber,
                        user.Name ?? string.Empty, string.Empty, user.Privilege, user.Enabled);
                    _typed.RefreshData(machineNumber);
                    return ok;
                }
                finally { _typed.EnableDevice(machineNumber, true); }
            }
            catch { return false; }
#else
            return false;   // device writes require the typed build
#endif
        }

        private bool DeleteUserCore(int machineNumber, string enrollNumber)
        {
            if (_device == null || string.IsNullOrWhiteSpace(enrollNumber)) return false;
#if ZK_TYPED
            try
            {
                _typed.EnableDevice(machineNumber, false);
                try
                {
                    // backupNumber 12 = delete the whole user (all fingers/card/password).
                    bool ok = _typed.SSR_DeleteEnrollData(machineNumber, enrollNumber, 12);
                    _typed.RefreshData(machineNumber);
                    return ok;
                }
                finally { _typed.EnableDevice(machineNumber, true); }
            }
            catch { return false; }
#else
            return false;
#endif
        }

        private int GetLastErrorCore()
        {
            if (_device == null) return -1;
            try
            {
                var args = new object[] { 0 };
                _type.InvokeMember("GetLastError", BindingFlags.InvokeMethod, null, _device, args);
                return Convert.ToInt32(args[0]);
            }
            catch
            {
                return -1;
            }
        }

        // SetDeviceTime(machine) sets the device clock to this PC's current time.
        // No out params, so it works late-bound too.
        private bool SyncTimeCore(int machineNumber)
        {
            if (_device == null) return false;
            try { return Convert.ToBoolean(Invoke("SetDeviceTime", machineNumber)); }
            catch { return false; }
        }

        private DateTime? GetDeviceTimeCore(int machineNumber)
        {
            if (_device == null) return null;
#if ZK_TYPED
            try
            {
                int y = 0, mo = 0, d = 0, h = 0, mi = 0, s = 0;
                if (_typed.GetDeviceTime(machineNumber, ref y, ref mo, ref d, ref h, ref mi, ref s))
                {
                    try { return new DateTime(y, mo, d, h, mi, s); }
                    catch { return null; }
                }
            }
            catch { /* fall through */ }
#endif
            // Late-bound GetDeviceTime can't marshal the ref params reliably (same issue as log reads).
            return null;
        }

        // ---- STA COM thread plumbing -------------------------------------------------

        private void EnsureComThread()
        {
            if (_dispatcher != null) return;
            lock (_threadGate)
            {
                if (_dispatcher != null) return;

                var ready = new ManualResetEventSlim(false);
                _comThread = new Thread(() =>
                {
                    _dispatcher = Dispatcher.CurrentDispatcher;
                    ready.Set();
                    Dispatcher.Run();   // pump messages so COM connection-point events arrive
                })
                {
                    IsBackground = true,
                    Name = "ZkDeviceCom"
                };
                _comThread.SetApartmentState(ApartmentState.STA);
                _comThread.Start();
                ready.Wait();
            }
        }

        private T Run<T>(Func<T> func)
        {
            EnsureComThread();
            return _dispatcher.Invoke(func);   // inline if already on the COM thread
        }

        private void Run(Action action)
        {
            EnsureComThread();
            _dispatcher.Invoke(action);
        }

        // ---- Helpers -----------------------------------------------------------------

        /// <summary>
        /// Plain TCP reachability probe — does the K70 answer on IP:port at all?
        /// Pure .NET, no SDK required; a failure here points at network/IP/firewall.
        /// </summary>
        public static bool ProbeTcp(string ip, int port, int timeoutMs, out string detail)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    var ar = client.BeginConnect(ip, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
                    {
                        detail = $"No response within {timeoutMs} ms — device off, wrong IP, or firewall blocking port {port}.";
                        return false;
                    }
                    client.EndConnect(ar);
                    detail = $"TCP port {port} is open on {ip}.";
                    return true;
                }
            }
            catch (Exception ex)
            {
                detail = ex.Message;
                return false;
            }
        }

        private Type _cachedType;

        private Type ResolveType()
        {
            if (_cachedType != null) return _cachedType;
            foreach (var progId in ProgIds)
            {
                var t = Type.GetTypeFromProgID(progId, throwOnError: false);
                if (t != null)
                {
                    _cachedType = t;
                    return t;
                }
            }
            return null;
        }

        private object Invoke(string method, params object[] args)
            => _type.InvokeMember(method, BindingFlags.InvokeMethod, null, _device, args);

        private void ReleaseDevice()
        {
            if (_device != null)
            {
                try { if (Marshal.IsComObject(_device)) Marshal.ReleaseComObject(_device); }
                catch { /* ignore */ }
                _device = null;
            }
#if ZK_TYPED
            _typed = null;
#endif
        }

#if ZK_TYPED
        /// <summary>
        /// Subscribe to real-time punch events. RegEvent must be called on every
        /// (re)connect — event registration does not survive a reconnect. Runs on the
        /// STA COM thread, so the connection-point callbacks are delivered here.
        /// </summary>
        private void WireLiveCapture(DeviceSettings s)
        {
            bool reg = _typed.RegEvent(s.MachineNumber, s.EventMask);
            if (!reg)
            {
                LastMessage += $" (RegEvent failed, SDK error {GetLastErrorCore()} — live capture inactive)";
                return;
            }
            _typed.OnAttTransactionEx += OnAttTransactionEx;
            LastMessage += " Live capture active.";
        }

        private void UnwireLiveCapture()
        {
            if (_typed == null) return;
            try { _typed.OnAttTransactionEx -= OnAttTransactionEx; }
            catch { /* best effort */ }
        }

        /// <summary>
        /// Fires on the STA COM thread for every successful punch. Keep it fast;
        /// subscribers (UI) must marshal to their own thread.
        /// Signature must match _IZKEMEvents_OnAttTransactionExEventHandler exactly.
        /// </summary>
        private void OnAttTransactionEx(string enrollNumber, int isInValid, int attState,
            int verifyMethod, int year, int month, int day,
            int hour, int minute, int second, int workCode)
        {
            DateTime ts;
            try { ts = new DateTime(year, month, day, hour, minute, second); }
            catch { ts = DateTime.Now; } // guard a bad device clock / malformed frame

            var punch = new PunchEvent
            {
                EnrollNumber = enrollNumber,
                IsValid      = isInValid == 0,   // per implementation plan mapping
                AttState     = attState,         // 0=check-in, 1=check-out, ...
                VerifyMethod = verifyMethod,     // 1=fingerprint, 2/3=card, 4=face, ...
                Timestamp    = ts,
                WorkCode     = workCode
            };

            PunchReceived?.Invoke(punch);
        }
#endif
    }
}
