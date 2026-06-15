using System;
using System.IO;
using System.Linq;
using DawloomAttendance.Data;
using DawloomAttendance.Data.Entities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DawloomAttendance.Tests
{
    /// <summary>
    /// Exercises the leave data layer against a real temporary SQLite database:
    /// seeded types, entitlement defaults/overrides, taken-day counting + dedup,
    /// and the per-date leave lookup used by the calc engine.
    /// </summary>
    [TestClass]
    public class AppDbLeaveTests
    {
        private string _dbPath;
        private AppDb _db;
        private long _annualId;

        [TestInitialize]
        public void Init()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), "dawloom_test_" + Guid.NewGuid().ToString("N") + ".db");
            _db = new AppDb(_dbPath);
            _db.Initialize();
            _annualId = _db.GetLeaveTypes().Single(t => t.Code == "annual").Id;
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { File.Delete(_dbPath); } catch { /* best effort */ }
        }

        [TestMethod]
        public void Initialize_SeedsFourStandardLeaveTypes()
        {
            var types = _db.GetLeaveTypes(activeOnly: true);

            CollectionAssert.AreEquivalent(
                new[] { "annual", "sick", "casual", "unpaid" },
                types.Select(t => t.Code).ToArray());

            var annual = types.Single(t => t.Code == "annual");
            Assert.IsTrue(annual.Paid);
            Assert.AreEqual(14.0, annual.DefaultDays, 0.001);

            Assert.IsFalse(types.Single(t => t.Code == "unpaid").Paid);
        }

        [TestMethod]
        public void GetLeaveBalances_FallsBackToTypeDefault_WhenNoOverride()
        {
            var annual = _db.GetLeaveBalances("100", 2026).Single(b => b.LeaveTypeId == _annualId);

            Assert.AreEqual(14.0, annual.Entitled, 0.001);
            Assert.AreEqual(0.0, annual.Taken, 0.001);
            Assert.AreEqual(14.0, annual.Remaining, 0.001);
        }

        [TestMethod]
        public void SetEntitlement_OverridesTheDefault_PerEmployeePerYear()
        {
            _db.SetEntitlement("100", _annualId, 2026, 20);

            Assert.AreEqual(20.0, _db.GetLeaveBalances("100", 2026).Single(b => b.LeaveTypeId == _annualId).Entitled, 0.001);
            // A different employee/year is unaffected.
            Assert.AreEqual(14.0, _db.GetLeaveBalances("100", 2025).Single(b => b.LeaveTypeId == _annualId).Entitled, 0.001);
            Assert.AreEqual(14.0, _db.GetLeaveBalances("200", 2026).Single(b => b.LeaveTypeId == _annualId).Entitled, 0.001);
        }

        [TestMethod]
        public void InsertLeaveEntry_CountsTaken_AndDedupsPerDay()
        {
            Assert.IsTrue(Add("100", new DateTime(2026, 6, 1)));
            Assert.IsTrue(Add("100", new DateTime(2026, 6, 2)));
            Assert.IsTrue(Add("100", new DateTime(2026, 6, 3)));
            // Same employee + same day again → ignored.
            Assert.IsFalse(Add("100", new DateTime(2026, 6, 3)));

            var annual = _db.GetLeaveBalances("100", 2026).Single(b => b.LeaveTypeId == _annualId);
            Assert.AreEqual(3.0, annual.Taken, 0.001);
            Assert.AreEqual(11.0, annual.Remaining, 0.001);

            // Days in another year don't count toward this year's taken.
            Add("100", new DateTime(2025, 6, 1));
            Assert.AreEqual(3.0, _db.GetLeaveBalances("100", 2026).Single(b => b.LeaveTypeId == _annualId).Taken, 0.001);
        }

        [TestMethod]
        public void GetLeaveOn_ReturnsTypeNameAndPaidFlag()
        {
            var date = new DateTime(2026, 6, 10);
            Add("100", date);

            var on = _db.GetLeaveOn(date);
            Assert.AreEqual(1, on.Count);
            Assert.AreEqual("100", on[0].Enroll);
            Assert.AreEqual("Annual", on[0].TypeName);
            Assert.IsTrue(on[0].Paid);
        }

        private bool Add(string enroll, DateTime date) =>
            _db.InsertLeaveEntry(new LeaveEntry { EnrollNumber = enroll, LeaveTypeId = _annualId, Date = date });
    }
}
