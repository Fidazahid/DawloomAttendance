using System;
using DawloomAttendance.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DawloomAttendance.Tests
{
    /// <summary>Monday–Saturday week math and previous-month boundaries.</summary>
    [TestClass]
    public class ReportPeriodsTests
    {
        // 2026-06-08 is a Monday; the Mon–Sun week containing a day starts on its Monday.
        [TestMethod]
        public void WeekStart_IsTheMonday()
        {
            Assert.AreEqual(new DateTime(2026, 6, 8),  ReportPeriods.WeekStart(new DateTime(2026, 6, 8)));  // Mon itself
            Assert.AreEqual(new DateTime(2026, 6, 8),  ReportPeriods.WeekStart(new DateTime(2026, 6, 10))); // Wed
            Assert.AreEqual(new DateTime(2026, 6, 8),  ReportPeriods.WeekStart(new DateTime(2026, 6, 13))); // Sat
            Assert.AreEqual(new DateTime(2026, 6, 8),  ReportPeriods.WeekStart(new DateTime(2026, 6, 14))); // Sun (still this week)
            Assert.AreEqual(new DateTime(2026, 6, 15), ReportPeriods.WeekStart(new DateTime(2026, 6, 15))); // next Mon
        }

        [TestMethod]
        public void PreviousWeek_IsThePriorMonToSat()
        {
            // On Monday 2026-06-08, the previous completed week is Mon 06-01 .. Sat 06-06.
            var (from, to) = ReportPeriods.PreviousWeek(new DateTime(2026, 6, 8));
            Assert.AreEqual(new DateTime(2026, 6, 1), from);
            Assert.AreEqual(new DateTime(2026, 6, 6), to);
            Assert.AreEqual(DayOfWeek.Monday, from.DayOfWeek);
            Assert.AreEqual(DayOfWeek.Saturday, to.DayOfWeek);
        }

        [TestMethod]
        public void PreviousWeek_SameForAnyDayWithinCurrentWeek()
        {
            // Mon..Sun of one week all share the same "previous week", so a missed Monday
            // send is still due on Tue/Wed/… of that week.
            var mon = ReportPeriods.PreviousWeek(new DateTime(2026, 6, 8));
            var wed = ReportPeriods.PreviousWeek(new DateTime(2026, 6, 10));
            var sun = ReportPeriods.PreviousWeek(new DateTime(2026, 6, 14));
            Assert.AreEqual(mon, wed);
            Assert.AreEqual(mon, sun);
        }

        [TestMethod]
        public void WeekKey_UsesMondayDate()
        {
            Assert.AreEqual("W2026-06-01", ReportPeriods.WeekKey(new DateTime(2026, 6, 1)));
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
