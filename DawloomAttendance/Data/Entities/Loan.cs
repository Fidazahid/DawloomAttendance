using System;

namespace DawloomAttendance.Data.Entities
{
    /// <summary>
    /// A loan/advance given to an employee. <see cref="Amount"/> is the total ("payment").
    /// <see cref="Installment"/> is the amount deducted per salary slip — 0 means the whole
    /// loan is deducted in one go on the next slip. <see cref="Deducted"/> is how much has
    /// already been taken out, so <see cref="Outstanding"/> = Amount − Deducted.
    /// </summary>
    public class Loan
    {
        public long Id { get; set; }
        public string EnrollNumber { get; set; }
        public DateTime Date { get; set; } = DateTime.Today;
        public double Amount { get; set; }
        public double Installment { get; set; }   // 0 = one-time full deduction
        public double Deducted { get; set; }
        public string Remarks { get; set; }
        public DateTime CreatedAt { get; set; }

        public double Outstanding => Math.Max(0, Amount - Deducted);
        public bool IsSettled => Outstanding <= 0.0001;

        /// <summary>What the next slip would take: the installment (capped at the balance), or the full balance.</summary>
        public double NextDeduction => Installment > 0 ? Math.Min(Installment, Outstanding) : Outstanding;

        /// <summary>For the grid: the installment amount, or "one-time" when deducted in full.</summary>
        public string InstallmentDisplay => Installment > 0 ? Installment.ToString("0.##") : "one-time";
    }

    /// <summary>One loan deduction shown on a salary slip (Date / Payment / Remarks).</summary>
    public class LoanLine
    {
        public DateTime Date { get; set; }
        public double Amount { get; set; }
        public string Remarks { get; set; }
    }

    /// <summary>Per-employee loan rollup for the Loans master table.</summary>
    public class LoanSummary
    {
        public string EnrollNumber { get; set; }
        public string Name { get; set; }
        public double TotalLoan { get; set; }     // sum of all loan amounts
        public double Outstanding { get; set; }   // sum of not-yet-deducted balances
        public int LoanCount { get; set; }
    }
}
