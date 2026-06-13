using System;

namespace DawloomAttendance.Data.Entities
{
    public class RawPunch
    {
        public long Id { get; set; }
        public string EnrollNumber { get; set; }
        public DateTime Timestamp { get; set; }
        public int AttState { get; set; }
        public int VerifyMethod { get; set; }
        public int WorkCode { get; set; }
        public bool IsValid { get; set; }
        public DateTime CapturedAt { get; set; }
        public string Source { get; set; }
    }
}
