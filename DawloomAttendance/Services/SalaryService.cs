using System;
using System.Collections.Generic;
using System.Globalization;
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
        /// Fills in Expected hours / Attendance % on an already-generated month whose slips
        /// were snapshotted before the working-hours attendance % shipped (the column
        /// migration left those at 0 / NULL, so the slip prints a blank and "—").
        ///
        /// Everything is derived from the snapshot itself, NOT recomputed from today's
        /// punches. That matters: a settled month's slip must stay internally consistent —
        /// the % has to be the one you get by dividing the Worked hours printed on that same
        /// page by the Expected hours next to it. Recomputing from live punch data drifts
        /// (the attendance calculator has changed since these were generated, and the current
        /// month keeps gaining punches), which would print a % that contradicts its own row.
        ///
        ///     expected = (WorkingDays - paid leave - unpaid leave) x shift length
        ///     percent  = WorkedHours / expected x 100
        ///
        /// Leave is subtracted because the payroll WorkingDays deliberately keeps leave in
        /// the divisor so paid leave is paid, whereas the attendance % excludes it (see
        /// <see cref="AttendancePercentage"/>). Pay, loan deductions and net pay are never
        /// touched — re-running GenerateMonth would deduct every outstanding loan twice.
        /// Returns the number of slips updated.
        /// </summary>
        public static int RecalculateAttendance(AppDb db, string periodKey)
        {
            var slips = db.GetSlipSnapshots(periodKey);
            if (slips.Count == 0) return 0;

            var rows = slips.Select(s =>
            {
                double shiftHours = ShiftHoursFromLabel(s.Shift);
                int workedDays = Math.Max(0, s.WorkingDays - s.PaidLeaveDays - s.UnpaidLeaveDays);
                double expected = workedDays * shiftHours;

                // No shift on the slip → the % is undefined, exactly as generation records it.
                double? pct = expected > 0
                    ? (double?)Math.Round(100.0 * s.WorkedHours / expected, 1)
                    : null;

                // Stored raw/uncapped; the 100% cap is applied when the slip is rendered.
                return (Enroll: s.Enroll, ExpectedHours: Math.Round(expected, 2), Pct: pct);
            });

            return db.UpdateSlipAttendance(periodKey, rows);
        }

        /// <summary>
        /// Shift length from the label frozen onto the slip ("New Shift (09:00-19:00)" → 10).
        /// The label is used rather than the employee's current shift because it records the
        /// shift they were actually on that month; reassigning someone since must not silently
        /// restate an old slip. Returns 0 for "—" / anything unparseable.
        /// </summary>
        private static double ShiftHoursFromLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return 0;
            var m = System.Text.RegularExpressions.Regex.Match(
                label, @"\((?<s>\d{1,2}:\d{2})\s*-\s*(?<e>\d{1,2}:\d{2})\)\s*$");
            if (!m.Success) return 0;
            if (!TimeSpan.TryParse(m.Groups["s"].Value, CultureInfo.InvariantCulture, out var st) ||
                !TimeSpan.TryParse(m.Groups["e"].Value, CultureInfo.InvariantCulture, out var en))
                return 0;

            double h = (en - st).TotalHours;
            if (h <= 0) h += 24;   // overnight shift
            return h;
        }

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
                    PdfExport.WriteSalarySlips(path, periodLabel, new[] { slip }, AttendancePercentage.AllowAbove100(db));
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
