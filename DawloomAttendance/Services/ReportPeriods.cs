using System;

namespace DawloomAttendance.Services
{
    /// <summary>
    /// Pure date math for the reporting periods. The work week runs <b>Monday→Saturday</b>
    /// (Sunday is the weekly off day). "Previous" always means the period immediately before
    /// the one containing the given day, so the just-finished week becomes "previous" on the
    /// next Monday — a report stays "due" all week until sent (a missed Monday is still caught
    /// on Tuesday, etc.).
    /// </summary>
    public static class ReportPeriods
    {
        /// <summary>The Monday that starts the Mon–Sun week containing <paramref name="day"/>.</summary>
        public static DateTime WeekStart(DateTime day)
        {
            // Monday=1 → 0 offset, Tuesday=2 → 1, … Sunday=0 → 6, Saturday=6 → 5.
            int daysSinceMonday = ((int)day.DayOfWeek + 6) % 7;
            return day.Date.AddDays(-daysSinceMonday);
        }

        /// <summary>
        /// The previous completed work week relative to <paramref name="today"/>, as
        /// <b>Monday→Saturday</b> (the off Sunday is excluded from the report range).
        /// </summary>
        public static (DateTime From, DateTime To) PreviousWeek(DateTime today)
        {
            var previousMonday = WeekStart(today).AddDays(-7);
            return (previousMonday, previousMonday.AddDays(5));   // Mon .. Sat
        }

        /// <summary>Stable key for a week, identified by its Monday start date.</summary>
        public static string WeekKey(DateTime weekFrom) => "W" + weekFrom.Date.ToString("yyyy-MM-dd");

        /// <summary>The previous calendar month relative to <paramref name="today"/> (first..last day).</summary>
        public static (DateTime From, DateTime To) PreviousMonth(DateTime today)
        {
            var firstOfThisMonth = new DateTime(today.Year, today.Month, 1);
            var to = firstOfThisMonth.AddDays(-1);
            var from = new DateTime(to.Year, to.Month, 1);
            return (from, to);
        }

        /// <summary>Stable key for a month, from any day within it.</summary>
        public static string MonthKey(DateTime monthDay) => "M" + monthDay.ToString("yyyy-MM");
    }
}
