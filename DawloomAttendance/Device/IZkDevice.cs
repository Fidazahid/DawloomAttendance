using System;
using System.Collections.Generic;
using DawloomAttendance.Device.Events;

namespace DawloomAttendance.Device
{
    public interface IZkDevice : IDisposable
    {
        DeviceConnectionState State { get; }

        /// <summary>Human-readable detail of the last operation (e.g. the connect failure reason).</summary>
        string LastMessage { get; }

        event Action<DeviceConnectionState> StateChanged;
        event Action<PunchEvent> PunchReceived;

        bool Connect(DeviceSettings settings);
        void Disconnect();

        bool Ping();

        IEnumerable<PunchEvent> ReadAllLogs(int machineNumber);

        /// <summary>Reads all users enrolled on the device.</summary>
        IEnumerable<DeviceUser> ReadAllUsers(int machineNumber);

        /// <summary>Creates/updates a user record on the device (not the fingerprint). Returns success.</summary>
        bool PushUser(int machineNumber, DeviceUser user);

        /// <summary>Removes a user (and their enrolled biometrics) from the device. Returns success.</summary>
        bool DeleteUser(int machineNumber, string enrollNumber);

        /// <summary>Sets the device clock to this PC's current time. Returns success.</summary>
        bool SyncTime(int machineNumber);

        /// <summary>Reads the device's current clock, or null if it can't be read.</summary>
        DateTime? GetDeviceTime(int machineNumber);

        int GetLastError();
    }
}
