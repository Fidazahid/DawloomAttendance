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
        public string Type { get; set; }          // category, e.g. Advance / Personal / Emergency
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

    /// <summary>
    /// One loan's deduction on a salary slip. <see cref="Date"/> is the date the loan was
    /// taken (identifies which loan on the slip); <see cref="Amount"/> is the installment paid
    /// this month; <see cref="PrevOutstanding"/>/<see cref="NewOutstanding"/> bracket it so the
    /// slip can show "previous remaining → paid → remaining".
    /// </summary>
    public class LoanLine
    {
        public DateTime Date { get; set; }          // loan taken date
        public string LoanType { get; set; }         // the loan's category
        public double Amount { get; set; }          // installment paid this month
        public string Remarks { get; set; }          // the user's note typed when the loan was created
        public double LoanAmount { get; set; }       // the loan's total
        public double Installment { get; set; }      // configured monthly installment (0 = one-time)
        public double PrevOutstanding { get; set; }  // balance before this month
        public double NewOutstanding { get; set; }   // balance after this month
        public string PeriodLabel { get; set; }      // salary month this deduction falls in, e.g. "April 2026"

        public bool IsSettled => NewOutstanding <= 0.0001;

        /// <summary>The app-generated deduction note shown on the slip / ledger for this line.</summary>
        public string DeductionNote => BuildDeductionNote(LoanAmount, Installment, NewOutstanding, PeriodLabel);

        /// <summary>
        /// Describes a loan deduction on a given salary month. One-time → "One-time deduction
        /// (full amount) · April 2026". Installment → "Installment 2 of 4 · Rs. 5,000/month ·
        /// April 2026", or in <paramref name="compact"/> form (used by the ledger) "Installment
        /// 2 of 4 · Rs. 5,000/ April 2026". The final installment adds "(settled)".
        /// </summary>
        public static string BuildDeductionNote(double loanAmount, double installment, double newOutstanding, string monthLabel, bool compact = false)
        {
            string month = string.IsNullOrWhiteSpace(monthLabel) ? null : monthLabel;

            if (installment <= 0.0001)
                return "One-time deduction (full amount)" + (month == null ? "" : " · " + month);

            int total = (int)Math.Ceiling(loanAmount / installment - 0.0001);
            int index = (int)Math.Ceiling((loanAmount - newOutstanding) / installment - 0.0001);
            if (index < 1) index = 1;
            if (index > total) index = total;

            string rate = month == null ? "/month" : (compact ? "/ " + month : "/month · " + month);
            string note = $"Installment {index} of {total} · Rs. {installment:#,0}{rate}";
            if (newOutstanding <= 0.0001) note += " (settled)";
            return note;
        }
    }

    /// <summary>
    /// One row of an employee's loan ledger: a loan taken (a <see cref="Debit"/>) or an
    /// installment paid on a slip (a <see cref="Credit"/>), with the running <see cref="Balance"/>.
    /// </summary>
    public class LoanLedgerEntry
    {
        public DateTime Date { get; set; }
        public string Type { get; set; }      // "Loan taken" / "Installment"
        public string Category { get; set; }  // the loan's type/category
        public string Period { get; set; }    // the month a payment was deducted (blank for a loan taken)
        public double Debit { get; set; }     // amount borrowed
        public double Credit { get; set; }    // amount repaid
        public double Balance { get; set; }   // running outstanding after this row
        public string Remarks { get; set; }

        public string DebitDisplay => Debit > 0 ? Debit.ToString("0") : "";
        public string CreditDisplay => Credit > 0 ? Credit.ToString("0") : "";
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
