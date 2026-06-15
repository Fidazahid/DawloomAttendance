using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DawloomAttendance.Data.Entities;

namespace DawloomAttendance.Services
{
    /// <summary>Turns computed attendance into per-employee payroll figures (shared by the table and slips).</summary>
    public static class PayrollCalculator
    {
        public static List<SalarySlip> Compute(
            IEnumerable<DailyAttendance> data,
            IDictionary<string, Employee> employees,
            IDictionary<string, Shift> shiftByEnroll,
            int latesPerDeduction,
            bool includeOvertime)
        {
            var slips = new List<SalarySlip>();

            foreach (var g in data.GroupBy(d => d.EnrollNumber))
            {
                employees.TryGetValue(g.Key, out var emp);
                shiftByEnroll.TryGetValue(g.Key, out var shift);

                double salary = emp?.Salary ?? 0;
                double shiftHours = ShiftHours(shift);

                // Leave days are scheduled work-days the person would otherwise work, so they
                // belong in the working-day divisor. Paid leave is then paid like a present day;
                // unpaid leave stays in the divisor but not the payable count, so it's deducted.
                // (A day where the person was on leave but still punched counts as present, not leave.)
                int paidLeave = g.Count(x => x.IsLeave && x.PaidLeave && !x.Present);
                int unpaidLeave = g.Count(x => x.IsLeave && !x.PaidLeave && !x.Present);

                int present = g.Count(x => x.Present);
                int workingDays = g.Count(x => x.IsWorkingDay) + paidLeave + unpaidLeave;
                int lateCount = g.Count(x => x.Late);
                double otHours = g.Sum(x => x.OvertimeHours);

                double lateDeductionDays = latesPerDeduction > 0 ? (double)lateCount / latesPerDeduction : 0;
                double payableDays = Math.Max(0, present + paidLeave - lateDeductionDays);

                double dailyRate = workingDays > 0 ? salary / workingDays : 0;
                double basePay = payableDays * dailyRate;
                double hourlyRate = shiftHours > 0 ? dailyRate / shiftHours : 0;
                double otPay = otHours * hourlyRate;

                slips.Add(new SalarySlip
                {
                    Enroll = g.Key,
                    Name = emp?.Name,
                    Cnic = emp?.Cnic,
                    Department = emp?.Department,
                    Designation = emp?.Designation,
                    Shift = shift == null ? "—" : $"{shift.Name} ({shift.StartTime}-{shift.EndTime})",
                    WorkingDays = workingDays,
                    Present = present,
                    Absent = g.Count(x => x.Absent),
                    LeaveDays = paidLeave + unpaidLeave,
                    PaidLeaveDays = paidLeave,
                    UnpaidLeaveDays = unpaidLeave,
                    LateCount = lateCount,
                    LateMinutes = g.Where(x => x.Late).Sum(x => x.LateMinutes),
                    WorkedHours = g.Sum(x => x.WorkedHours),
                    OvertimeHours = otHours,
                    LateDeductionDays = lateDeductionDays,
                    PayableDays = payableDays,
                    Salary = salary,
                    DailyRate = dailyRate,
                    BasePay = basePay,
                    OvertimePay = otPay,
                    IncludeOvertime = includeOvertime,
                    NetPay = basePay + (includeOvertime ? otPay : 0)
                })
                ;
            }

            return slips.OrderBy(s => SortKey(s.Enroll)).ToList();
        }

        public static double ShiftHours(Shift s)
        {
            if (s == null) return 0;
            if (TimeSpan.TryParseExact(s.StartTime, @"hh\:mm", CultureInfo.InvariantCulture, out var st) &&
                TimeSpan.TryParseExact(s.EndTime, @"hh\:mm", CultureInfo.InvariantCulture, out var en))
            {
                double h = (en - st).TotalHours;
                if (h <= 0) h += 24;
                return h;
            }
            return 0;
        }

        private static int SortKey(string enroll) => int.TryParse(enroll, out var n) ? n : int.MaxValue;
    }
}
