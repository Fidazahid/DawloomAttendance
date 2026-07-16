using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DawloomAttendance.Data;
using DawloomAttendance.Data.Entities;

namespace DawloomAttendance.Services
{
    /// <summary>
    /// Orchestrates salary-slip generation and sending. A month is generated once — its
    /// slips (with loan deductions applied) are snapshotted so re-generating reproduces
    /// them exactly and never deducts a loan twice. Sending skips interns (employees with
    /// no salary) and anyone without an email address.
    /// </summary>
    public static class SalaryService
    {
        /// <summary>The (key, first-day, last-day) of the calendar month containing <paramref name="anyDay"/>.</summary>
        public static (string PeriodKey, DateTime From, DateTime To) Month(DateTime anyDay)
        {
            var from = new DateTime(anyDay.Year, anyDay.Month, 1);
            var to = from.AddMonths(1).AddDays(-1);
            return (ReportPeriods.MonthKey(from), from, to);
        }

        /// <summary>
        /// Generates a month's slips if not already generated (computing attendance/pay and
        /// applying outstanding loans, then snapshotting), or returns the existing snapshot.
        /// </summary>
        public static IReadOnlyList<SalarySlip> GenerateMonth(AppDb db, DateTime from, DateTime to,
            bool includeOvertime = true, bool applyDeduction = true)
        {
            var periodKey = ReportPeriods.MonthKey(from);
            if (db.SalaryBatchExists(periodKey))
                return db.GetSlipSnapshots(periodKey);   // already generated → exact snapshot

            var names = db.GetEmployees().ToDictionary(e => e.EnrollNumber);
            var shifts = db.GetShifts().ToDictionary(s => s.Id);
            var shiftByEnroll = names.Values.ToDictionary(e => e.EnrollNumber,
                e => e.ShiftId.HasValue && shifts.TryGetValue(e.ShiftId.Value, out var sh) ? sh : null);

            int lpd = 3;
            var lpdSetting = db.GetSetting("LatesPerDeduction");
            if (int.TryParse(lpdSetting, out var n) && n > 0) lpd = n;

            var data = new AttendanceService(db).ComputeForRange(from, to);
            var baseSlips = PayrollCalculator.Compute(data, names, shiftByEnroll, lpd, includeOvertime, applyDeduction);

            return db.GenerateAndSaveMonth(periodKey, from, to, baseSlips);
        }

        /// <summary>The stored slips for a generated month (used to re-generate the PDF or email).</summary>
        public static IReadOnlyList<SalarySlip> Load(AppDb db, string periodKey) => db.GetSlipSnapshots(periodKey);

        /// <summary>
        /// Emails each slip to its employee. Interns (no salary in the record) are skipped
        /// and reported, as are employees without a valid email address.
        /// </summary>
        public static List<EmailSendOutcome> SendSlips(AppDb db, EmailSettings email, string periodLabel,
            IReadOnlyList<SalarySlip> slips, IDictionary<string, Employee> names, IProgress<string> progress = null)
        {
            var outcomes = new List<EmailSendOutcome>();
            int i = 0, total = slips.Count;
            foreach (var slip in slips)
            {
                i++;
                names.TryGetValue(slip.Enroll, out var emp);
                progress?.Report($"Sending {i}/{total}: {slip.Name ?? slip.Enroll} …");

                // No salary on the record → intern → do not send.
                if (slip.Salary <= 0)
                {
                    outcomes.Add(EmailSendOutcome.Fail(slip.Enroll, slip.Name, emp?.Email, "no salary (intern) — not sent"));
                    continue;
                }

                string addr = emp?.Email;
                if (!EmailService.LooksLikeEmail(addr))
                {
                    outcomes.Add(EmailSendOutcome.Fail(slip.Enroll, slip.Name, addr, "no email address"));
                    continue;
                }

                // Attachment name the employee sees: EmployeeName_salarySlip_Month_YYYY.pdf.
                // A unique temp subfolder keeps that friendly name without risking collisions.
                var dir = Path.Combine(Path.GetTempPath(), "DawloomSlips", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                var fileName = $"{Safe(slip.Name ?? slip.Enroll)}_salarySlip_{Safe(periodLabel)}.pdf";
                var path = Path.Combine(dir, fileName);
                try
                {
                    PdfExport.WriteSalarySlips(path, periodLabel, new[] { slip });
                    var subject = EmailService.FormatSubject(email.SubjectSlip, slip.Name ?? slip.Enroll, periodLabel);
                    var body = $"Dear {slip.Name},\r\n\r\nPlease find attached your salary slip for {periodLabel}.\r\n\r\nRegards,\r\n{email.FromNameSalary}";
                    EmailService.Send(email, addr, subject, body, path, email.FromNameSalary);
                    outcomes.Add(EmailSendOutcome.Ok(slip.Enroll, slip.Name, addr));
                }
                catch (Exception ex)
                {
                    outcomes.Add(EmailSendOutcome.Fail(slip.Enroll, slip.Name, addr, ex.Message));
                }
                finally { try { Directory.Delete(dir, true); } catch { /* temp files */ } }
            }
            return outcomes;
        }

        private static string Safe(string s) =>
            string.Concat((s ?? "x").Select(c => char.IsLetterOrDigit(c) ? c : '_'));
    }
}
