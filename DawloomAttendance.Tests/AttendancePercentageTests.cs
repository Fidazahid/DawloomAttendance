using System;
using System.Collections.Generic;
using DawloomAttendance.Data.Entities;
using DawloomAttendance.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DawloomAttendance.Tests
{
    /// <summary>
    /// The working-hours attendance percentage: expected = working days x shift hours,
    /// actual = hours worked. Weekends/holidays/leave are already excluded upstream
    /// (AttendanceService clears IsWorkingDay), so they must not be counted here.
    /// </summary>
    [TestClass]
    public class AttendancePercentageTests
    {
        private static readonly DateTime Start = new DateTime(2026, 6, 1);

        // 09:00-18:00 = 9 hours.
        private static Shift NineHour() => new Shift { Name = "Day", StartTime = "09:00", EndTime = "18:00" };

        private static DailyAttendance Working(int dayOffset, double worked, bool present = true) =>
            new DailyAttendance
            {
                Date = Start.AddDays(dayOffset),
                IsWorkingDay = true,
                Present = present,
                Absent = !present,
                WorkedHours = worked
            };

        private static DailyAttendance Off(int dayOffset, double worked = 0) =>
            new DailyAttendance
            {
                Date = Start.AddDays(dayOffset),
                IsWorkingDay = false,
                Present = worked > 0,
                Category = DayCategory.Off,
                WorkedHours = worked
            };

        [TestMethod]
        public void FullAttendance_IsExactlyOneHundred()
        {
            var days = new List<DailyAttendance> { Working(0, 9), Working(1, 9), Working(2, 9) };

            var r = AttendancePercentage.Compute(days, NineHour(), allowAbove100: false);

            Assert.AreEqual(3, r.WorkingDays);
            Assert.AreEqual(27, r.ExpectedHours);
            Assert.AreEqual(27, r.ActualHours);
            Assert.AreEqual(100.0, r.Percent);
        }

        [TestMethod]
        public void OffDaysAreExcludedFromExpectedHours()
        {
            // 2 working days + a weekend day that was NOT worked: divisor is 2 x 9 = 18.
            var days = new List<DailyAttendance> { Working(0, 9), Working(1, 9), Off(2) };

            var r = AttendancePercentage.Compute(days, NineHour(), allowAbove100: false);

            Assert.AreEqual(2, r.WorkingDays);
            Assert.AreEqual(18, r.ExpectedHours);
            Assert.AreEqual(100.0, r.Percent);
        }

        [TestMethod]
        public void WorkOnAnOffDay_PushesAboveOneHundred_WhenAllowed()
        {
            // The old days-based formula produced >100% silently; now it is opt-in.
            var days = new List<DailyAttendance> { Working(0, 9), Working(1, 9), Off(2, worked: 9) };

            var allowed = AttendancePercentage.Compute(days, NineHour(), allowAbove100: true);
            var capped = AttendancePercentage.Compute(days, NineHour(), allowAbove100: false);

            Assert.AreEqual(27, allowed.ActualHours);
            Assert.AreEqual(18, allowed.ExpectedHours);
            Assert.AreEqual(150.0, allowed.Percent);
            Assert.AreEqual(100.0, capped.Percent, "unchecked must cap the display at 100%");
        }

        [TestMethod]
        public void MissingCheckoutDay_ScoresZeroHours_AndIsCounted()
        {
            // Punched in, never out → WorkedHours 0. Present stays true so pay is untouched;
            // the day is surfaced so the low score is explainable.
            var days = new List<DailyAttendance>
            {
                Working(0, 9),
                Working(1, 0),          // check-in only
                Working(2, 9),
            };

            var r = AttendancePercentage.Compute(days, NineHour(), allowAbove100: false);

            Assert.AreEqual(1, r.MissingCheckoutDays);
            Assert.AreEqual(18, r.ActualHours);
            Assert.AreEqual(27, r.ExpectedHours);
            Assert.AreEqual(66.7, r.Percent);
        }

        [TestMethod]
        public void AbsentDay_IsNotCountedAsMissingCheckout()
        {
            // Never showed up at all — that is an absence, not a missing punch.
            var days = new List<DailyAttendance> { Working(0, 9), Working(1, 0, present: false) };

            var r = AttendancePercentage.Compute(days, NineHour(), allowAbove100: false);

            Assert.AreEqual(0, r.MissingCheckoutDays);
            Assert.AreEqual(50.0, r.Percent);
        }

        [TestMethod]
        public void NoShift_LeavesPercentUndefined()
        {
            var days = new List<DailyAttendance> { Working(0, 8) };

            var r = AttendancePercentage.Compute(days, null, allowAbove100: false);

            Assert.IsNull(r.Percent, "no shift means no expected hours, so the ratio is undefined");
            Assert.AreEqual("—", r.Display);
        }

        [TestMethod]
        public void NoWorkingDays_LeavesPercentUndefined()
        {
            var days = new List<DailyAttendance> { Off(0), Off(1) };

            var r = AttendancePercentage.Compute(days, NineHour(), allowAbove100: false);

            Assert.IsNull(r.Percent);
        }

        [TestMethod]
        public void TenHourShift_UsesTheEmployeesOwnShiftLength()
        {
            var tenHour = new Shift { Name = "Long", StartTime = "08:00", EndTime = "18:00" };
            var days = new List<DailyAttendance> { Working(0, 9), Working(1, 9) };

            var r = AttendancePercentage.Compute(days, tenHour, allowAbove100: false);

            Assert.AreEqual(20, r.ExpectedHours);
            Assert.AreEqual(90.0, r.Percent);
        }
    }
}
