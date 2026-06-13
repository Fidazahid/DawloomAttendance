using System;

namespace DawloomAttendance.Device.Events
{
    public class PunchEvent
    {
        public string EnrollNumber { get; set; }
        public DateTime Timestamp { get; set; }
        public int AttState { get; set; }
        public int VerifyMethod { get; set; }
        public int WorkCode { get; set; }
        public bool IsValid { get; set; }

        public string AttStateLabel
        {
            get
            {
                switch (AttState)
                {
                    case 0: return "Check-In";
                    case 1: return "Check-Out";
                    case 2: return "Break-Out";
                    case 3: return "Break-In";
                    case 4: return "Overtime-In";
                    case 5: return "Overtime-Out";
                    default: return "State " + AttState;
                }
            }
        }

        public string VerifyMethodLabel
        {
            get
            {
                switch (VerifyMethod)
                {
                    case 0: return "password";
                    case 1: return "fingerprint";
                    case 2: return "card";
                    case 3: return "card";
                    case 4: return "face";
                    default: return "verify " + VerifyMethod;
                }
            }
        }
    }
}
