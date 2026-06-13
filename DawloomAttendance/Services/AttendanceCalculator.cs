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

            var ordered = (punches ?? Enumerable.Empty<RawPunch>())
                .Where(p => p.Timestamp.Date == date.Date)
                .OrderBy(p => p.Timestamp)
                .ToList();

            da.PunchCount = ordered.Count;
            if (ordered.Count > 0)
            {
                da.FirstPunch = ordered.First().Timestamp;
                da.Present = true;

                // Check-out only exists when there's a distinct later punch. A lone
                // check-in leaves check-out (and worked hours) empty.
                if (ordered.Count >= 2)
                {
                    da.LastPunch = ordered.Last().Timestamp;
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
            var start = ParseTime(shift?.StartTime);
            var end = ParseTime(shift?.EndTime);
            if (shift != null && start.HasValue && end.HasValue)
            {
                var shiftStart = date.Date + start.Value;
                var shiftEnd = date.Date + end.Value;
                if (shiftEnd <= shiftStart) shiftEnd = shiftEnd.AddDays(1); // overnight shift

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
