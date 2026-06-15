using DawloomAttendance.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DawloomAttendance.Tests
{
    /// <summary>Pure helpers behind the email feature: subject templating and address sanity.</summary>
    [TestClass]
    public class EmailServiceTests
    {
        [TestMethod]
        public void FormatSubject_SubstitutesNameAndPeriod()
        {
            var s = EmailService.FormatSubject("Weekly — {Name} ({Period})", "Ali Khan", "2026-06-06 to 2026-06-12");
            Assert.AreEqual("Weekly — Ali Khan (2026-06-06 to 2026-06-12)", s);
        }

        [TestMethod]
        public void FormatSubject_HandlesNulls()
        {
            Assert.AreEqual("Report —  ()", EmailService.FormatSubject("Report — {Name} ({Period})", null, null));
        }

        [TestMethod]
        public void LooksLikeEmail_AcceptsValid_RejectsInvalid()
        {
            Assert.IsTrue(EmailService.LooksLikeEmail("ali@example.com"));
            Assert.IsTrue(EmailService.LooksLikeEmail("  a.b@dawloom.pk "));

            Assert.IsFalse(EmailService.LooksLikeEmail(""));
            Assert.IsFalse(EmailService.LooksLikeEmail(null));
            Assert.IsFalse(EmailService.LooksLikeEmail("noatsign"));
            Assert.IsFalse(EmailService.LooksLikeEmail("@no-local.com"));
            Assert.IsFalse(EmailService.LooksLikeEmail("no-domain@"));
            Assert.IsFalse(EmailService.LooksLikeEmail("no-dot@domain"));
        }

        [TestMethod]
        public void IsConfigured_RequiresHostAndFrom()
        {
            Assert.IsFalse(new EmailSettings().IsConfigured);
            Assert.IsFalse(new EmailSettings { Host = "smtp.x.com" }.IsConfigured);
            Assert.IsTrue(new EmailSettings { Host = "smtp.x.com", FromAddress = "a@x.com" }.IsConfigured);
        }
    }
}
