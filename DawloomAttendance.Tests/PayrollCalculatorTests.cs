using System.Collections.Generic;
using System.Linq;
using DawloomAttendance.Data.Entities;
using DawloomAttendance.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DawloomAttendance.Tests
{
    /// <summary>
    /// Covers the leave-aware payroll math: paid leave is still paid, unpaid leave is
    /// deducted, and a day worked while "on leave" counts as present (not double-counted).
    /// </summary>
    [TestClass]
    public class PayrollCalculatorTests
    {
        private const string Enroll = "1";
        private const double Salary = 1000.0;

        private static DailyAttendance Day(bool working, bool present,
            bool leave = false, bool paidLeave = false, bool absent = false) =>
            new DailyAttendance
            {
                EnrollNumber = Enroll,
                IsWorkingDay = working,
                Present = present,
                Absent = absent,
                IsLeave = leave,
                PaidLeave = paidLeave
            };

        private static SalarySlip Run(IEnumerable<DailyAttendance> days)
        {
            var employees = new Dictionary<string, Employee>
            {
                [Enroll] = new Employee { EnrollNumber = Enroll, Name = "Test", Salary = Salary }
            };
            var shifts = new Dictionary<string, Shift>();   // no shift → overtime pay is 0, base pay unaffected
            return PayrollCalculator.Compute(days, employees, shifts, latesPerDeduction: 0, includeOvertime: false)
                .Single();
        }

        [TestMethod]
        public void AllPresent_PaysFullSalary()
        {
            var days = Enumerable.Range(0, 10).Select(_ => Day(working: true, present: true));
            var slip = Run(days);

            Assert.AreEqual(10, slip.WorkingDays);
            Assert.AreEqual(10, slip.Present);
            Assert.AreEqual(Salary, slip.NetPay, 0.001);
            Assert.AreEqual(0, slip.LeaveDays);
        }

        [TestMethod]
        public void PaidLeave_CountsTowardPay_NoLoss()
        {
            // 8 worked + 2 paid-leave days out of a 10-day working period.
            var days = Enumerable.Range(0, 8).Select(_ => Day(working: true, present: true)).ToList();
            days.Add(Day(working: false, present: false, leave: true, paidLeave: true));
            days.Add(Day(working: false, present: false, leave: true, paidLeave: true));

            var slip = Run(days);

            Assert.AreEqual(10, slip.WorkingDays, "paid leave days belong in the working-day divisor");
            Assert.AreEqual(10.0, slip.PayableDays, 0.001, "paid leave is payable");
            Assert.AreEqual(Salary, slip.NetPay, 0.001, "no pay is lost for paid leave");
            Assert.AreEqual(2, slip.PaidLeaveDays);
            Assert.AreEqual(0, slip.UnpaidLeaveDays);
            Assert.AreEqual(2, slip.LeaveDays);
        }

        [TestMethod]
        public void UnpaidLeave_IsDeducted()
        {
            // 8 worked + 2 unpaid-leave days out of a 10-day working period.
            var days = Enumerable.Range(0, 8).Select(_ => Day(working: true, present: true)).ToList();
            days.Add(Day(working: false, present: false, leave: true, paidLeave: false));
            days.Add(Day(working: false, present: false, leave: true, paidLeave: false));

            var slip = Run(days);

            Assert.AreEqual(10, slip.WorkingDays, "unpaid leave still counts as a scheduled work day");
            Assert.AreEqual(8.0, slip.PayableDays, 0.001, "unpaid leave is not payable");
            Assert.AreEqual(Salary * 0.8, slip.NetPay, 0.001, "two unpaid days dock 2/10 of salary");
            Assert.AreEqual(2, slip.UnpaidLeaveDays);
            Assert.AreEqual(0, slip.PaidLeaveDays);
        }

        [TestMethod]
        public void PaidVsUnpaid_DiffersByExactlyTheDeductedDays()
        {
            var paid = Run(Enumerable.Range(0, 8).Select(_ => Day(true, true))
                .Append(Day(false, false, leave: true, paidLeave: true))
                .Append(Day(false, false, leave: true, paidLeave: true)));

            var unpaid = Run(Enumerable.Range(0, 8).Select(_ => Day(true, true))
                .Append(Day(false, false, leave: true, paidLeave: false))
                .Append(Day(false, false, leave: true, paidLeave: false)));

            // Both have the same 10-day divisor; the only difference is 2 payable days.
            Assert.AreEqual(2 * (Salary / 10.0), paid.NetPay - unpaid.NetPay, 0.001);
        }

        [TestMethod]
        public void WorkedWhileOnLeave_CountsAsPresent_NotDoubleCounted()
        {
            // A day flagged paid-leave but the person actually punched in (Present = true).
            var days = Enumerable.Range(0, 9).Select(_ => Day(working: true, present: true)).ToList();
            days.Add(Day(working: false, present: true, leave: true, paidLeave: true));

            var slip = Run(days);

            Assert.AreEqual(10, slip.Present, "the worked day is counted as present");
            Assert.AreEqual(0, slip.PaidLeaveDays, "a present day is not also counted as leave");
        }
    }
}
