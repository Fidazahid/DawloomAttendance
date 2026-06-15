using System;
using System.Collections.Generic;
using System.Linq;
using DawloomAttendance.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DawloomAttendance.Tests
{
    /// <summary>Summary/daily computation for the per-employee emailed report.</summary>
    [TestClass]
    public class EmployeeReportTests
    {
        private static DailyAttendance Work(DateTime d, bool present, bool late = false, int lateMin = 0, double worked = 0) =>
            new DailyAttendance { Date = d, IsWorkingDay = true, Present = present, Absent = !present, Late = late, LateMinutes = lateMin, WorkedHours = worked };

        private static DailyAttendance Leave(DateTime d, bool paid) =>
            new DailyAttendance { Date = d, IsWorkingDay = false, IsLeave = true, PaidLeave = paid, Category = DayCategory.Off };

        [TestMethod]
        public void Summary_CountsAttendanceCorrectly()
        {
            var d = new DateTime(2026, 6, 1);
            var days = new List<DailyAttendance>
            {
                Work(d,          present: true,  late: true, lateMin: 20, worked: 8),
                Work(d.AddDays(1), present: true,  worked: 8),
                Work(d.AddDays(2), present: false),                 // absent
                Leave(d.AddDays(3), paid: true),
                Leave(d.AddDays(4), paid: false),
            };

            var summary = EmployeeReport.BuildSummary(days)
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            Assert.AreEqual("3", summary["Working days"]);   // 3 working days
            Assert.AreEqual("2", summary["Present"]);
            Assert.AreEqual("1", summary["Absent"]);
            Assert.AreEqual("1", summary["Paid leave"]);
            Assert.AreEqual("1", summary["Unpaid leave"]);
            Assert.AreEqual("1", summary["Late count"]);
            Assert.AreEqual("66.7%", summary["Attendance %"]);  // 2/3
        }

        [TestMethod]
        public void DailyRows_AreOldestFirst_AndMatchHeaderWidth()
        {
            var d = new DateTime(2026, 6, 10);
            var days = new List<DailyAttendance>
            {
                Work(d.AddDays(1), present: true, worked: 8),
                Work(d,            present: true, worked: 8),
            };

            var rows = EmployeeReport.BuildDailyRows(days);

            Assert.AreEqual(2, rows.Count);
            Assert.AreEqual("2026-06-10", rows[0][0]);   // oldest first
            Assert.AreEqual("2026-06-11", rows[1][0]);
            Assert.IsTrue(rows.All(r => r.Count == EmployeeReport.DailyHeaders.Length));
        }
    }
}
