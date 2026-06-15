using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DawloomAttendance.Data;
using DawloomAttendance.Data.Entities;

namespace DawloomAttendance.Services
{
    /// <summary>How a set of recipients splits before sending: who to send, who's already done, who has no email.</summary>
    public class RecipientPlan
    {
        public List<Employee> ToSend { get; } = new List<Employee>();
        public List<Employee> AlreadySent { get; } = new List<Employee>();
        public List<Employee> MissingEmail { get; } = new List<Employee>();
    }

    /// <summary>
    /// Builds per-employee report PDFs and emails them, recording each outcome in the
    /// sent-log. Used by the manual "Send via Email" button and the on-launch auto-send.
    /// </summary>
    public static class ReportMailer
    {
        /// <summary>
        /// Classifies recipients: those already sent this period (when <paramref name="skipAlreadySent"/>),
        /// those missing/invalid email, and the rest to send. Pure decision logic (no email I/O).
        /// </summary>
        public static RecipientPlan Plan(AppDb db, IEnumerable<Employee> recipients,
            string kind, string periodKey, bool skipAlreadySent)
        {
            var plan = new RecipientPlan();
            foreach (var e in recipients ?? Enumerable.Empty<Employee>())
            {
                if (skipAlreadySent && !string.IsNullOrEmpty(periodKey) && db.WasEmailSent(e.EnrollNumber, kind, periodKey))
                    plan.AlreadySent.Add(e);
                else if (!EmailService.LooksLikeEmail(e.Email))
                    plan.MissingEmail.Add(e);
                else
                    plan.ToSend.Add(e);
            }
            return plan;
        }

        /// <summary>
        /// Sends the period's report to each recipient (skipping already-sent when asked),
        /// returning one outcome per recipient. Missing-email recipients are reported, not sent.
        /// </summary>
        public static List<EmailSendOutcome> Send(AppDb db, EmailSettings email, IEnumerable<Employee> recipients,
            DateTime from, DateTime to, string subjectTemplate, string kind, string periodKey, bool skipAlreadySent,
            string fromName = null)
        {
            fromName = string.IsNullOrWhiteSpace(fromName) ? email.FromNameReports : fromName;
            var outcomes = new List<EmailSendOutcome>();
            var plan = Plan(db, recipients, kind, periodKey, skipAlreadySent);

            foreach (var e in plan.MissingEmail)
                outcomes.Add(EmailSendOutcome.Fail(e.EnrollNumber, e.Name, e.Email, "no email address"));

            if (plan.ToSend.Count == 0) return outcomes;

            // Compute attendance once for the whole range, then slice per employee.
            var all = new AttendanceService(db).ComputeForRange(from, to);
            var byEnroll = all.GroupBy(d => d.EnrollNumber)
                .ToDictionary(g => g.Key, g => (IList<DailyAttendance>)g.ToList());

            string period = $"{from:yyyy-MM-dd} to {to:yyyy-MM-dd}";

            foreach (var e in plan.ToSend)
            {
                byEnroll.TryGetValue(e.EnrollNumber, out var days);
                days = days ?? new List<DailyAttendance>();

                var path = Path.Combine(Path.GetTempPath(),
                    $"Dawloom_{Safe(e.EnrollNumber)}_{kind}_{Guid.NewGuid():N}.pdf");
                try
                {
                    EmployeeReport.WritePdf(path, e, from, to, days);
                    var subject = EmailService.FormatSubject(subjectTemplate, e.Name ?? e.EnrollNumber, period);
                    var body = $"Dear {e.Name ?? "employee"},\r\n\r\nPlease find attached your attendance report for {period}.\r\n\r\nRegards,\r\n{fromName}";
                    EmailService.Send(email, e.Email, subject, body, path, fromName);

                    if (!string.IsNullOrEmpty(periodKey)) db.RecordEmailSent(e.EnrollNumber, kind, periodKey, true, null);
                    outcomes.Add(EmailSendOutcome.Ok(e.EnrollNumber, e.Name, e.Email));
                }
                catch (Exception ex)
                {
                    if (!string.IsNullOrEmpty(periodKey)) db.RecordEmailSent(e.EnrollNumber, kind, periodKey, false, ex.Message);
                    outcomes.Add(EmailSendOutcome.Fail(e.EnrollNumber, e.Name, e.Email, ex.Message));
                }
                finally
                {
                    try { File.Delete(path); } catch { /* temp file */ }
                }
            }
            return outcomes;
        }

        private static string Safe(string s) =>
            string.Concat((s ?? "x").Select(c => char.IsLetterOrDigit(c) ? c : '_'));
    }
}
