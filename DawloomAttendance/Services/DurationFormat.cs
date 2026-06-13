using System;

namespace DawloomAttendance.Services
{
    /// <summary>Formats durations as "Xh Ym" (e.g. 4h 5m). Empty string for zero/none.</summary>
    public static class DurationFormat
    {
        public static string Hours(double hours)
        {
            if (hours <= 0) return "";
            return Minutes((int)Math.Round(hours * 60));
        }

        public static string Minutes(int minutes)
        {
            if (minutes <= 0) return "";
            int h = minutes / 60, m = minutes % 60;
            if (h == 0) return $"{m}m";
            if (m == 0) return $"{h}h";
            return $"{h}h {m}m";
        }
    }
}
