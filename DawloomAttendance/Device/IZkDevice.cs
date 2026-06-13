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

        int GetLastError();
    }
}
