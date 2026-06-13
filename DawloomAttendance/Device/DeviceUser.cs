namespace DawloomAttendance.Device
{
    /// <summary>A user enrolled on the device (read via SSR_GetAllUserInfo).</summary>
    public class DeviceUser
    {
        public string EnrollNumber { get; set; }
        public string Name { get; set; }
        public int Privilege { get; set; }   // 0=user, 14=admin (SDK codes)
        public bool Enabled { get; set; }
    }
}
