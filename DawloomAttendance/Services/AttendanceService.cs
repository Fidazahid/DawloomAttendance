using System;
using System.Collections.Generic;
using System.Linq;
using DawloomAttendance.Data;
using DawloomAttendance.Data.Entities;

namespace DawloomAttendance.Services
{
    /// <summary>
    /// DB-driven attendance: pulls punches/shifts/holidays and runs the pure
    /// <see cref="AttendanceCalculator"/> for each active employee on a date.
    /// </summary>
    public class AttendanceService
    {
        private readonly AppDb _db;

        public AttendanceService(AppDb db) { _db = db; }

        public List<DailyAttendance> ComputeForDate(DateTime date)
        {
            var employees = _db.GetEmployees().Where(e => e.Active).ToList();
            var shifts = _db.GetShifts().ToDictionary(s => s.Id);
            var globalWeekend = _db.ResolveWeeklyOffDays(date);   // month-overrides-year

            // Holidays on this date: company-wide (null enroll) and per-employee.
            var hols = _db.GetHolidaysOn(date);
            bool companyHoliday = hols.Any(h => string.IsNullOrEmpty(h.Key));
            string companyReason = hols.Where(h => string.IsNullOrEmpty(h.Key)).Select(h => h.Value).FirstOrDefault();
            var employeeHoliday = hols.Where(h => !string.IsNullOrEmpty(h.Key))
                .GroupBy(h => h.Key).ToDictionary(g => g.Key, g => g.First().Value);

            // Recorded leave on this date, keyed by employee (one leave per person per day).
            var leaveByEnroll = _db.GetLeaveOn(date)
                .GroupBy(l => l.Enroll)
                .ToDictionary(g => g.Key, g => g.First());

            // Overnight shifts span into the next calendar day, so pull tomorrow's
            // punches too when any employee works one — the calculator pairs the
            // late check-in with the next-morning check-out on this work-date.
            bool anyOvernight = employees.Any(e =>
                e.ShiftId.HasValue && shifts.TryGetValue(e.ShiftId.Value, out var os)
                && AttendanceCalculator.IsOvernightShift(os));

            IEnumerable<RawPunch> dayPunches = _db.GetPunchesForDate(date);
            if (anyOvernight)
                dayPunches = dayPunches.Concat(_db.GetPunchesForDate(date.AddDays(1)));

            var punchesByEnroll = dayPunches
                .GroupBy(p => p.EnrollNumber)
                .ToDictionary(g => g.Key, g => (IEnumerable<RawPunch>)g.ToList());

            var results = new List<DailyAttendance>();
            foreach (var e in employees)
            {
                Shift shift = (e.ShiftId.HasValue && shifts.TryGetValue(e.ShiftId.Value, out var s)) ? s : null;

                // Weekend precedence: a shift's own weekend days override the global
                // weekly off-days; with no shift (or no weekend set) fall back to global.
                var weekend = ParseWeekend(shift?.WeekendDays) ?? globalWeekend;
                bool isWeekend = weekend.Contains((int)date.DayOfWeek);

                bool empHoliday = employeeHoliday.TryGetValue(e.EnrollNumber, out var empReason);
                bool isHoliday = companyHoliday || empHoliday;

                // Typed leave takes the off-reason over a weekend, but a company/employee
                // holiday still wins (the person is off regardless, and balance isn't spent).
                bool hasLeave = leaveByEnroll.TryGetValue(e.EnrollNumber, out var leave) && !isHoliday;

                string offReason = isHoliday
                    ? (companyHoliday ? (companyReason ?? "Holiday") : (empReason ?? "Leave"))
                    : hasLeave ? leave.TypeName + " Leave"
                    : (isWeekend ? "Weekend" : null);

                bool isOff = isWeekend || isHoliday || hasLeave;

                punchesByEnroll.TryGetValue(e.EnrollNumber, out var punches);
                var da = AttendanceCalculator.Calculate(e.EnrollNumber, date, punches, shift, isOff, offReason);
                da.IsLeave = hasLeave;
                da.PaidLeave = hasLeave && leave.Paid;
                results.Add(da);
            }
            return results;
        }

        /// <summary>Computes attendance for every day in [from, to] (inclusive), all employees.</summary>
        public List<DailyAttendance> ComputeForRange(DateTime from, DateTime to)
        {
            var all = new List<DailyAttendance>();
            for (var d = from.Date; d <= to.Date; d = d.AddDays(1))
                all.AddRange(ComputeForDate(d));
            return all;
        }

        private static HashSet<int> ParseWeekend(string csv)
        {
            if (string.IsNullOrWhiteSpace(csv)) return null;
            var set = new HashSet<int>();
            foreach (var tok in csv.Split(','))
                if (int.TryParse(tok.Trim(), out var d) && d >= 0 && d <= 6)
                    set.Add(d);
            return set.Count == 0 ? null : set;
        }
    }
}
