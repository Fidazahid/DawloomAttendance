using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DawloomAttendance.Data.Entities;

namespace DawloomAttendance.Services
{
    /// <summary>
    /// Pure attendance logic — no DB, no device, fully unit-testable.
    /// (RawPunches + Shift + day-type) → DailyAttendance.
    ///
    /// Conventions:
    ///  - First/last punch of the day bound the worked interval (gross hours).
    ///  - Late if first punch is more than the shift's grace period after start;
    ///    LateMinutes is total minutes after scheduled start.
    ///  - Overtime is minutes after scheduled end; on an off day, all worked time is overtime.
    ///  - Half-day if worked less than half the shift length; otherwise full day.
    /// </summary>
    public static class AttendanceCalculator
    {
        // For an overnight shift, the work-date window starts this long before the
        // scheduled start so a slightly-early check-in still counts toward the right day.
        // The window is exactly 24h wide, so days never overlap or drop a punch.
        private static readonly TimeSpan OvernightLookback = TimeSpan.FromHours(3);

        // Punches the device tags as a closing punch (check-out / overtime-out). If a day
        // has at least one of these, we pair first→last; if it has none (only check-ins),
        // it's "check-in only" so duplicate check-ins aren't mistaken for a check-out.
        private static readonly HashSet<int> OutStates = new HashSet<int> { 1, 5 };  // Check-Out, Overtime-Out

        /// <summary>True when the shift's end-of-day is at/before its start (it crosses midnight).</summary>
        public static bool IsOvernightShift(Shift shift)
        {
            var s = ParseTime(shift?.StartTime);
            var e = ParseTime(shift?.EndTime);
            return s.HasValue && e.HasValue && e.Value <= s.Value;
        }

        public static DailyAttendance Calculate(
            string enrollNumber, DateTime date, IEnumerable<RawPunch> punches, Shift shift,
            bool isNonWorkingDay, string offReason = null)
        {
            var da = new DailyAttendance
            {
                EnrollNumber = enrollNumber,
                Date = date.Date,
                IsWorkingDay = !isNonWorkingDay,
                OffReason = isNonWorkingDay ? offReason : null
            };

            // Resolve the shift window up front; it also decides how we bucket punches.
            var start = ParseTime(shift?.StartTime);
            var end = ParseTime(shift?.EndTime);
            bool hasShiftTimes = shift != null && start.HasValue && end.HasValue;

            DateTime shiftStart = default, shiftEnd = default;
            bool overnight = false;
            if (hasShiftTimes)
            {
                shiftStart = date.Date + start.Value;
                shiftEnd = date.Date + end.Value;
                if (shiftEnd <= shiftStart) { shiftEnd = shiftEnd.AddDays(1); overnight = true; }
            }

            // Pick the punches belonging to this work-date. A normal shift uses the
            // calendar day; an overnight shift uses a 24h window anchored just before
            // the shift start, so tonight's check-in and tomorrow morning's check-out
            // are paired on the day the shift began (instead of looking like two
            // separate "check-in only" days).
            IEnumerable<RawPunch> inWindow;
            if (overnight)
            {
                var windowStart = shiftStart - OvernightLookback;
                var windowEnd = windowStart.AddDays(1);
                inWindow = (punches ?? Enumerable.Empty<RawPunch>())
                    .Where(p => p.Timestamp >= windowStart && p.Timestamp < windowEnd);
            }
            else
            {
                inWindow = (punches ?? Enumerable.Empty<RawPunch>())
                    .Where(p => p.Timestamp.Date == date.Date);
            }

            var ordered = inWindow.OrderBy(p => p.Timestamp).ToList();

            da.PunchCount = ordered.Count;
            if (ordered.Count > 0)
            {
                da.Present = true;

                // IN  = first punch, OUT = last punch (by time) — paired only when the day
                //       actually contains a check-out punch. If every punch is a check-in
                //       (badged in once or several times, never out), it's "check-in only"
                //       with no hours, so two or three check-ins are never mistaken for a
                //       check-out. Mis-ordered/mis-tagged punches still pair by time.
                var first = ordered.First();
                var last = ordered.Last();
                bool hasCheckout = ordered.Any(p => OutStates.Contains(p.AttState));

                da.FirstPunch = first.Timestamp;

                if (hasCheckout && last.Timestamp > first.Timestamp)
                {
                    da.LastPunch = last.Timestamp;
                    da.WorkedHours = Math.Round((da.LastPunch.Value - da.FirstPunch.Value).TotalHours, 2);
                }
                else
                {
                    da.Notes = "Check-in only (no check-out)";
                }
            }

            // Weekend / holiday: any time worked is overtime; never "absent".
            if (isNonWorkingDay)
            {
                da.Category = DayCategory.Off;
                da.OvertimeHours = da.WorkedHours;
                return da;
            }

            // Working day with no punches → absent.
            if (!da.Present)
            {
                da.Absent = true;
                da.Category = DayCategory.Absent;
                return da;
            }

            // Working day with punches → classify against the shift if we have valid times.
            if (hasShiftTimes)
            {
                // Lateness is judged from the check-in (available even without a check-out).
                double arrivalDelay = (da.FirstPunch.Value - shiftStart).TotalMinutes;
                if (arrivalDelay > shift.GraceMinutes)
                {
                    da.Late = true;
                    da.LateMinutes = (int)Math.Round(arrivalDelay);
                }

                if (da.LastPunch.HasValue)
                {
                    double earlyBy = (shiftEnd - da.LastPunch.Value).TotalMinutes;
                    if (earlyBy > 0)
                    {
                        da.EarlyDeparture = true;
                        da.EarlyMinutes = (int)Math.Round(earlyBy);
                    }

                    double overtime = (da.LastPunch.Value - shiftEnd).TotalMinutes;
                    if (overtime > 0) da.OvertimeHours = Math.Round(overtime / 60.0, 2);

                    double shiftHours = (shiftEnd - shiftStart).TotalHours;
                    da.Category = da.WorkedHours < shiftHours / 2.0 ? DayCategory.HalfDay : DayCategory.FullDay;
                }
                else
                {
                    da.Category = DayCategory.HalfDay;   // no check-out → incomplete
                }
            }
            else
            {
                // No shift (or bad times): present, but late/early can't be judged.
                da.Category = da.LastPunch.HasValue ? DayCategory.FullDay : DayCategory.HalfDay;
                da.Notes = Append(da.Notes, "No shift assigned");
            }

            return da;
        }

        private static TimeSpan? ParseTime(string hhmm)
            => TimeSpan.TryParseExact(hhmm, @"hh\:mm", CultureInfo.InvariantCulture, out var t) ? t : (TimeSpan?)null;

        private static string Append(string notes, string add)
            => string.IsNullOrEmpty(notes) ? add : notes + "; " + add;
    }
}
