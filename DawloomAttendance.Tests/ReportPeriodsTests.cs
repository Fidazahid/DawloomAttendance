using System;
using DawloomAttendance.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DawloomAttendance.Tests
{
    /// <summary>Saturday–Friday week math and previous-month boundaries.</summary>
    [TestClass]
    public class ReportPeriodsTests
    {
        // 2026-06-08 is a Monday; the Sat–Fri week containing it starts Sat 2026-06-06.
        [TestMethod]
        public void WeekStart_IsTheSaturday()
        {
            Assert.AreEqual(new DateTime(2026, 6, 6), ReportPeriods.WeekStart(new DateTime(2026, 6, 8)));  // Mon
            Assert.AreEqual(new DateTime(2026, 6, 6), ReportPeriods.WeekStart(new DateTime(2026, 6, 6)));  // Sat itself
            Assert.AreEqual(new DateTime(2026, 6, 6), ReportPeriods.WeekStart(new DateTime(2026, 6, 12))); // Fri (end)
            Assert.AreEqual(new DateTime(2026, 6, 13), ReportPeriods.WeekStart(new DateTime(2026, 6, 13))); // next Sat
        }

        [TestMethod]
        public void PreviousWeek_IsThePriorSatToFri()
        {
            // On Monday 2026-06-08, the previous completed week is Sat 05-30 .. Fri 06-05.
            var (from, to) = ReportPeriods.PreviousWeek(new DateTime(2026, 6, 8));
            Assert.AreEqual(new DateTime(2026, 5, 30), from);
            Assert.AreEqual(new DateTime(2026, 6, 5), to);
            Assert.AreEqual(DayOfWeek.Saturday, from.DayOfWeek);
            Assert.AreEqual(DayOfWeek.Friday, to.DayOfWeek);
        }

        [TestMethod]
        public void PreviousWeek_SameForAnyDayWithinCurrentWeek()
        {
            // Monday and the following Wednesday/Friday are in the same Sat–Fri week,
            // so "previous week" is identical — a missed Monday is still due on Tue/Wed.
            var mon = ReportPeriods.PreviousWeek(new DateTime(2026, 6, 8));
            var wed = ReportPeriods.PreviousWeek(new DateTime(2026, 6, 10));
            var fri = ReportPeriods.PreviousWeek(new DateTime(2026, 6, 12));
            Assert.AreEqual(mon, wed);
            Assert.AreEqual(mon, fri);
        }

        [TestMethod]
        public void WeekKey_UsesSaturdayDate()
        {
            Assert.AreEqual("W2026-05-30", ReportPeriods.WeekKey(new DateTime(2026, 5, 30)));
        }

        [TestMethod]
        public void PreviousMonth_IsFirstToLastOfPriorMonth()
        {
            var (from, to) = ReportPeriods.PreviousMonth(new DateTime(2026, 6, 15));
            Assert.AreEqual(new DateTime(2026, 5, 1), from);
            Assert.AreEqual(new DateTime(2026, 5, 31), to);

            // January rolls back to December of the prior year.
            var (jf, jt) = ReportPeriods.PreviousMonth(new DateTime(2026, 1, 3));
            Assert.AreEqual(new DateTime(2025, 12, 1), jf);
            Assert.AreEqual(new DateTime(2025, 12, 31), jt);
        }

        [TestMethod]
        public void MonthKey_FormatsYearMonth()
        {
            Assert.AreEqual("M2026-05", ReportPeriods.MonthKey(new DateTime(2026, 5, 1)));
        }
    }
}
