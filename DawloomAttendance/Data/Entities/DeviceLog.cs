using System;

namespace DawloomAttendance.Data.Entities
{
    public class DeviceLog
    {
        public long Id { get; set; }
        public DateTime Timestamp { get; set; }
        public string Level { get; set; }
        public string Event { get; set; }
        public string Detail { get; set; }
    }
}
