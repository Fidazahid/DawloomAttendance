using System;
using System.IO;
using System.Linq;
using DawloomAttendance.Data;
using DawloomAttendance.Data.Entities;
using DawloomAttendance.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DawloomAttendance.Tests
{
    /// <summary>
    /// End-to-end through the DB-driven service: a recorded leave turns the day into a
    /// typed off-day and stamps the paid/unpaid flags the payroll layer relies on.
    /// </summary>
    [TestClass]
    public class AttendanceServiceLeaveTests
    {
        private string _dbPath;
        private AppDb _db;

        [TestInitialize]
        public void Init()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), "dawloom_test_" + Guid.NewGuid().ToString("N") + ".db");
            _db = new AppDb(_dbPath);
            _db.Initialize();
            _db.InsertEmployee(new Employee { EnrollNumber = "100", Name = "Tester", Active = true });
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { File.Delete(_dbPath); } catch { /* best effort */ }
        }

        [TestMethod]
        public void RecordedLeave_MakesDayOff_WithTypedReasonAndPaidFlag()
        {
            var date = new DateTime(2026, 6, 10);   // no weekly-off / holiday configured → a plain working day
            long annualId = _db.GetLeaveTypes().Single(t => t.Code == "annual").Id;
            _db.InsertLeaveEntry(new LeaveEntry { EnrollNumber = "100", LeaveTypeId = annualId, Date = date });

            var day = new AttendanceService(_db).ComputeForDate(date).Single(d => d.EnrollNumber == "100");

            Assert.IsTrue(day.IsLeave);
            Assert.IsTrue(day.PaidLeave, "annual is a paid type");
            Assert.AreEqual(DayCategory.Off, day.Category);
            Assert.IsFalse(day.IsWorkingDay);
            Assert.AreEqual("Annual Leave", day.OffReason);
        }

        [TestMethod]
        public void UnpaidLeave_IsFlaggedNotPaid()
        {
            var date = new DateTime(2026, 6, 11);
            long unpaidId = _db.GetLeaveTypes().Single(t => t.Code == "unpaid").Id;
            _db.InsertLeaveEntry(new LeaveEntry { EnrollNumber = "100", LeaveTypeId = unpaidId, Date = date });

            var day = new AttendanceService(_db).ComputeForDate(date).Single(d => d.EnrollNumber == "100");

            Assert.IsTrue(day.IsLeave);
            Assert.IsFalse(day.PaidLeave, "unpaid leave must not be flagged paid");
            Assert.AreEqual("Unpaid Leave", day.OffReason);
        }
    }
}
