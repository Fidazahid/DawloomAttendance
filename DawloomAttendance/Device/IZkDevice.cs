using System;
using System.Collections.Generic;
using DawloomAttendance.Device.Events;

namespace DawloomAttendance.Device
{
    public interface IZkDevice : IDisposable
    {
        DeviceConnectionState State { get; }
        event Action<DeviceConnectionState> StateChanged;
        event Action<PunchEvent> PunchReceived;

        bool Connect(DeviceSettings settings);
        void Disconnect();

        bool Ping();

        IEnumerable<PunchEvent> ReadAllLogs(int machineNumber);

        /// <summary>Sets the device clock to this PC's current time. Returns success.</summary>
        bool SyncTime(int machineNumber);

        /// <summary>Reads the device's current clock, or null if it can't be read.</summary>
        DateTime? GetDeviceTime(int machineNumber);

        int GetLastError();
    }
}
