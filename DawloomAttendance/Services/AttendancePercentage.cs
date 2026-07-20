using System;
using System.Collections.Generic;
using System.Linq;
using DawloomAttendance.Data.Entities;

namespace DawloomAttendance.Services
{
    /// <summary>
    /// The one and only attendance-percentage formula. Every screen, export and
    /// email must go through here — the reports used to each carry their own copy
    /// of the maths and silently drifted apart.
    ///
    /// Working-hours based:
    ///     working days   = days that are actually scheduled work (AttendanceService
    ///                      already clears IsWorkingDay for weekends, holidays AND
    ///                      approved leave, so no extra exclusion is needed here)
    ///     expected hours = working days x shift length
    ///     actual hours   = hours worked across the period
    ///     percentage     = actual / expected x 100
    ///
    /// A day with a check-in but no check-out has WorkedHours = 0 (see
    /// AttendanceCalculator), so it scores as absent for this percentage while
    /// leaving Present — and therefore pay — untouched. Those days are counted in
    /// <see cref="Result.MissingCheckoutDays"/> so a low score can be explained
    /// (and fixed with Edit Punches) instead of looking like a bug.
    /// </summary>
    public static class AttendancePercentage
    {
        /// <summary>Setting key for the app-wide "Allow Percentage Above 100%" choice.</summary>
        public const string AllowAbove100Key = "Report.AllowPercentAbove100";

        public sealed class Result
        {
            public int WorkingDays { get; set; }
            public double ShiftHours { get; set; }
            public double ExpectedHours { get; set; }
            public double ActualHours { get; set; }

            /// <summary>Working days where the person punched in but never out (0 hours credited).</summary>
            public int MissingCheckoutDays { get; set; }

            /// <summary>Null when undefined (no working days, or the employee has no shift).</summary>
            public double? Percent { get; set; }

            public string Display => Percent.HasValue ? Percent.Value.ToString("0.#") + "%" : "—";
        }

        /// <summary>
        /// Hours worked on off days (weekend/holiday/leave) count toward the total, which
        /// is how a genuine score can exceed 100% — that is what allowAbove100 governs.
        /// </summary>
        public static Result Compute(IEnumerable<DailyAttendance> days, Shift shift, bool allowAbove100)
        {
            var list = days?.ToList() ?? new List<DailyAttendance>();

            int workingDays = list.Count(d => d.IsWorkingDay);
            double shiftHours = PayrollCalculator.ShiftHours(shift);
            double expected = workingDays * shiftHours;
            double actual = list.Sum(d => d.WorkedHours);

            double? pct = expected > 0 ? (double?)Math.Round(100.0 * actual / expected, 1) : null;
            if (pct.HasValue && !allowAbove100) pct = Math.Min(pct.Value, 100.0);

            return new Result
            {
                WorkingDays = workingDays,
                ShiftHours = shiftHours,
                ExpectedHours = Math.Round(expected, 2),
                ActualHours = Math.Round(actual, 2),
                MissingCheckoutDays = list.Count(d => d.IsWorkingDay && d.Present && d.WorkedHours <= 0),
                Percent = pct
            };
        }

        /// <summary>Reads the persisted app-wide cap choice (default: cap at 100%).</summary>
        public static bool AllowAbove100(Data.AppDb db)
        {
            var raw = db?.GetSetting(AllowAbove100Key);
            return bool.TryParse(raw, out var b) && b;
        }

        public static void SetAllowAbove100(Data.AppDb db, bool value)
            => db?.SetSetting(AllowAbove100Key, value ? "True" : "False");
    }
}
