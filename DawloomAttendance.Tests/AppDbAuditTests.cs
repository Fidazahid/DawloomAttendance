using System;
using System.IO;
using System.Linq;
using DawloomAttendance.Data;
using DawloomAttendance.Data.Entities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DawloomAttendance.Tests
{
    /// <summary>
    /// Verifies the audit trail against a real temp SQLite DB: data mutations record
    /// entries attributed to the current actor, and the query honours date + actor filters.
    /// </summary>
    [TestClass]
    public class AppDbAuditTests
    {
        private string _dbPath;
        private AppDb _db;

        [TestInitialize]
        public void Init()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), "dawloom_test_" + Guid.NewGuid().ToString("N") + ".db");
            _db = new AppDb(_dbPath);
            _db.Initialize();
            AppDb.CurrentActor = "tester";   // deterministic actor for assertions
        }

        [TestCleanup]
        public void Cleanup()
        {
            AppDb.CurrentActor = Environment.UserName;   // restore for other tests
            try { File.Delete(_dbPath); } catch { /* best effort */ }
        }

        private System.Collections.Generic.IReadOnlyList<AuditEntry> Today() =>
            _db.GetAuditLog(DateTime.Today, DateTime.Today);

        [TestMethod]
        public void AddingEmployee_RecordsAuditEntry_WithActor()
        {
            _db.InsertEmployee(new Employee { EnrollNumber = "100", Name = "Ali", Active = true });

            var entry = Today().Single(a => a.Action == "Employee added");
            Assert.AreEqual("tester", entry.Actor);
            Assert.AreEqual("Employee", entry.Entity);
            StringAssert.Contains(entry.Detail, "100");
            StringAssert.Contains(entry.Detail, "Ali");
        }

        [TestMethod]
        public void EmployeeLifecycle_RecordsAddUpdateDelete()
        {
            long id = _db.InsertEmployee(new Employee { EnrollNumber = "100", Name = "Ali", Active = true });
            _db.UpdateEmployee(new Employee { Id = id, EnrollNumber = "100", Name = "Ali Khan", Active = true });
            _db.DeleteEmployee(id);

            var actions = Today().Select(a => a.Action).ToList();
            CollectionAssert.IsSubsetOf(
                new[] { "Employee added", "Employee updated", "Employee deleted" },
                actions);
        }

        [TestMethod]
        public void LeaveAdd_RecordsAuditEntry()
        {
            long annual = _db.GetLeaveTypes().Single(t => t.Code == "annual").Id;
            _db.InsertLeaveEntry(new LeaveEntry { EnrollNumber = "100", LeaveTypeId = annual, Date = DateTime.Today });

            Assert.AreEqual(1, Today().Count(a => a.Action == "Leave added"));
        }

        [TestMethod]
        public void ActorFilter_ReturnsOnlyThatUsersEntries()
        {
            _db.InsertEmployee(new Employee { EnrollNumber = "100", Name = "Ali", Active = true });
            AppDb.CurrentActor = "someone-else";
            _db.InsertEmployee(new Employee { EnrollNumber = "101", Name = "Sara", Active = true });

            var mine = _db.GetAuditLog(DateTime.Today, DateTime.Today, "tester");
            Assert.IsTrue(mine.All(a => a.Actor == "tester"));
            Assert.IsTrue(mine.Any());
            Assert.IsFalse(mine.Any(a => a.Actor == "someone-else"));

            CollectionAssert.Contains(_db.GetAuditActors().ToArray(), "someone-else");
        }

        [TestMethod]
        public void DateFilter_ExcludesEntriesOutsideRange()
        {
            _db.InsertEmployee(new Employee { EnrollNumber = "100", Name = "Ali", Active = true });

            // A window entirely in the past has no entries; today's window has them.
            var past = _db.GetAuditLog(DateTime.Today.AddDays(-10), DateTime.Today.AddDays(-5));
            Assert.AreEqual(0, past.Count);
            Assert.IsTrue(Today().Count > 0);
        }
    }
}
