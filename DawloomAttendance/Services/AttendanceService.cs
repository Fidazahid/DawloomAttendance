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
            bool datedHoliday = _db.IsDatedHoliday(date);

            var punchesByEnroll = _db.GetPunchesForDate(date)
                .GroupBy(p => p.EnrollNumber)
                .ToDictionary(g => g.Key, g => (IEnumerable<RawPunch>)g.ToList());

            var results = new List<DailyAttendance>();
            foreach (var e in employees)
            {
                Shift shift = (e.ShiftId.HasValue && shifts.TryGetValue(e.ShiftId.Value, out var s)) ? s : null;

                // Weekend precedence: a shift's own weekend days override the global
                // weekly off-days; with no shift (or no weekend set) fall back to global.
                var weekend = ParseWeekend(shift?.WeekendDays) ?? globalWeekend;
                bool isOff = datedHoliday || weekend.Contains((int)date.DayOfWeek);

                punchesByEnroll.TryGetValue(e.EnrollNumber, out var punches);
                results.Add(AttendanceCalculator.Calculate(e.EnrollNumber, date, punches, shift, isOff));
            }
            return results;
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
