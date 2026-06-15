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
    /// Sent-log round-trip and recipient classification (send vs already-sent vs missing-email).
    /// The actual SMTP send isn't exercised here — that needs a live server.
    /// </summary>
    [TestClass]
    public class ReportMailerTests
    {
        private string _dbPath;
        private AppDb _db;

        [TestInitialize]
        public void Init()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), "dawloom_test_" + Guid.NewGuid().ToString("N") + ".db");
            _db = new AppDb(_dbPath);
            _db.Initialize();
        }

        [TestCleanup]
        public void Cleanup() { try { File.Delete(_dbPath); } catch { } }

        [TestMethod]
        public void EmailLog_RoundTrips_ByEnrollKindPeriod()
        {
            Assert.IsFalse(_db.WasEmailSent("100", "weekly", "W2026-06-06"));

            _db.RecordEmailSent("100", "weekly", "W2026-06-06", ok: true, detail: null);
            Assert.IsTrue(_db.WasEmailSent("100", "weekly", "W2026-06-06"));

            // Different employee / kind / period are independent.
            Assert.IsFalse(_db.WasEmailSent("101", "weekly", "W2026-06-06"));
            Assert.IsFalse(_db.WasEmailSent("100", "monthly", "W2026-06-06"));
            Assert.IsFalse(_db.WasEmailSent("100", "weekly", "W2026-06-13"));
        }

        [TestMethod]
        public void FailedSend_DoesNotCountAsSent()
        {
            _db.RecordEmailSent("100", "weekly", "W2026-06-06", ok: false, detail: "smtp error");
            Assert.IsFalse(_db.WasEmailSent("100", "weekly", "W2026-06-06"),
                "a failed attempt must not be treated as already-sent (so it retries)");
        }

        [TestMethod]
        public void Plan_SplitsSendAlreadySentAndMissingEmail()
        {
            var withEmail = new Employee { EnrollNumber = "100", Name = "Ali", Email = "ali@example.com" };
            var noEmail = new Employee { EnrollNumber = "101", Name = "Sara", Email = null };
            var alreadySent = new Employee { EnrollNumber = "102", Name = "Omar", Email = "omar@example.com" };

            _db.RecordEmailSent("102", "weekly", "W2026-06-06", ok: true, detail: null);

            var plan = ReportMailer.Plan(_db, new[] { withEmail, noEmail, alreadySent },
                "weekly", "W2026-06-06", skipAlreadySent: true);

            CollectionAssert.AreEqual(new[] { "100" }, plan.ToSend.Select(e => e.EnrollNumber).ToArray());
            CollectionAssert.AreEqual(new[] { "101" }, plan.MissingEmail.Select(e => e.EnrollNumber).ToArray());
            CollectionAssert.AreEqual(new[] { "102" }, plan.AlreadySent.Select(e => e.EnrollNumber).ToArray());
        }

        [TestMethod]
        public void Plan_WithoutSkip_IncludesAlreadySent()
        {
            var e = new Employee { EnrollNumber = "102", Name = "Omar", Email = "omar@example.com" };
            _db.RecordEmailSent("102", "weekly", "W2026-06-06", ok: true, detail: null);

            var plan = ReportMailer.Plan(_db, new[] { e }, "weekly", "W2026-06-06", skipAlreadySent: false);

            Assert.AreEqual(1, plan.ToSend.Count);
            Assert.AreEqual(0, plan.AlreadySent.Count);
        }
    }
}
