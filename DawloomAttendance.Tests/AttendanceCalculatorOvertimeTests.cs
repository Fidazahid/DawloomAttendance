using System;
using System.Collections.Generic;
using DawloomAttendance.Data.Entities;
using DawloomAttendance.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DawloomAttendance.Tests
{
    /// <summary>
    /// Verifies overtime and overnight handling for the exact scenarios asked about:
    ///  1) Day shift, check in 09:00 / check out 21:00  -> overtime after shift end.
    ///  2) Cross-midnight work, check in 23:00 / out 01:00 next day, under a NORMAL
    ///     day shift (shows it does NOT pair) and under an OVERNIGHT shift (works).
    /// AttState: 0 = Check-In, 1 = Check-Out, 5 = Overtime-Out.
    /// </summary>
    [TestClass]
    public class AttendanceCalculatorOvertimeTests
    {
        private static Shift DayShift() => new Shift
        {
            Name = "Day 9-18", StartTime = "09:00", EndTime = "18:00",
            GraceMinutes = 15, WeekendDays = "0"
        };

        // End <= Start => overnight (crosses midnight).
        private static Shift NightShift() => new Shift
        {
            Name = "Night 22-06", StartTime = "22:00", EndTime = "06:00",
            GraceMinutes = 15, WeekendDays = "0"
        };

        private static RawPunch Punch(DateTime ts, int attState) =>
            new RawPunch { EnrollNumber = "1", Timestamp = ts, AttState = attState, IsValid = true };

        // ---- Scenario 1: day shift, in 09:00, out 21:00 -> 3h overtime ----------
        [TestMethod]
        public void DayShift_InNine_OutTwentyOne_GivesThreeHoursOvertime()
        {
            var date = new DateTime(2026, 7, 1);
            var punches = new List<RawPunch>
            {
                Punch(date.AddHours(9),  0),   // check-in 09:00
                Punch(date.AddHours(21), 1),   // check-out 21:00
            };

            var da = AttendanceCalculator.Calculate("1", date, punches, DayShift(), isNonWorkingDay: false);

            Assert.AreEqual(12.0, da.WorkedHours, 0.001, "gross worked = 21:00-09:00");
            Assert.AreEqual(3.0, da.OvertimeHours, 0.001, "overtime = time after 18:00 end");
            Assert.IsFalse(da.Late, "arrived exactly on start");
            Assert.AreEqual(DayCategory.FullDay, da.Category);
        }

        // ---- Scenario 2a: cross-midnight under a NORMAL day shift (does NOT pair) -
        [TestMethod]
        public void NormalShift_CrossMidnight_DoesNotPair_CheckInOnly()
        {
            var day = new DateTime(2026, 7, 1);
            var punches = new List<RawPunch>
            {
                Punch(day.AddHours(23),            0),   // in  2026-07-01 23:00
                Punch(day.AddDays(1).AddHours(1),  1),   // out 2026-07-02 01:00
            };

            // A normal day shift only looks at punches whose calendar Date == the work-date,
            // so the 01:00 next-day check-out is NOT seen -> check-in only, no hours/overtime.
            var da = AttendanceCalculator.Calculate("1", day, punches, DayShift(), isNonWorkingDay: false);

            Assert.AreEqual(1, da.PunchCount, "only the 23:00 punch falls on 2026-07-01");
            Assert.AreEqual(0.0, da.WorkedHours, 0.001, "no check-out seen -> no worked hours");
            Assert.AreEqual(0.0, da.OvertimeHours, 0.001, "no overtime without a paired check-out");
        }

        // ---- Scenario 2b: same punches under an OVERNIGHT shift (works) ----------
        [TestMethod]
        public void NightShift_In2300_Out0100_PairsAcrossMidnight()
        {
            var day = new DateTime(2026, 7, 1);
            var punches = new List<RawPunch>
            {
                Punch(day.AddHours(23),            0),   // in  2026-07-01 23:00
                Punch(day.AddDays(1).AddHours(1),  1),   // out 2026-07-02 01:00
            };

            var da = AttendanceCalculator.Calculate("1", day, punches, NightShift(), isNonWorkingDay: false);

            Assert.AreEqual(2, da.PunchCount, "24h overnight window captures both punches");
            Assert.AreEqual(2.0, da.WorkedHours, 0.001, "01:00 - 23:00 = 2h");
            Assert.AreEqual(0.0, da.OvertimeHours, 0.001, "left 01:00, before 06:00 end -> no OT");
            Assert.IsTrue(da.Late, "23:00 arrival is after 22:00 start + grace");
        }

        // ---- Scenario 2c: overnight shift with real overtime past the end --------
        [TestMethod]
        public void NightShift_StaysPastEnd_GivesOvertime()
        {
            var day = new DateTime(2026, 7, 1);
            var punches = new List<RawPunch>
            {
                Punch(day.AddHours(22),            0),   // in  2026-07-01 22:00 (on time)
                Punch(day.AddDays(1).AddHours(8),  5),   // out 2026-07-02 08:00 (2h past 06:00 end)
            };

            var da = AttendanceCalculator.Calculate("1", day, punches, NightShift(), isNonWorkingDay: false);

            Assert.AreEqual(10.0, da.WorkedHours, 0.001, "22:00 -> 08:00 = 10h gross");
            Assert.AreEqual(2.0, da.OvertimeHours, 0.001, "08:00 - 06:00 end = 2h overtime");
            Assert.IsFalse(da.Late, "arrived on time");
        }

        // ---- Off-day (weekend/holiday): all worked time is overtime --------------
        [TestMethod]
        public void OffDay_AllWorkedTimeIsOvertime()
        {
            var date = new DateTime(2026, 7, 5); // a Sunday in this scenario
            var punches = new List<RawPunch>
            {
                Punch(date.AddHours(10), 0),
                Punch(date.AddHours(14), 1),
            };

            var da = AttendanceCalculator.Calculate("1", date, punches, DayShift(),
                isNonWorkingDay: true, offReason: "Weekend");

            Assert.AreEqual(4.0, da.WorkedHours, 0.001);
            Assert.AreEqual(4.0, da.OvertimeHours, 0.001, "on an off day, all worked time is OT");
            Assert.IsFalse(da.Absent);
        }
    }
}
