using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using DawloomAttendance.Data.Entities;
using DawloomAttendance.Services;

namespace DawloomAttendance.Data
{
    /// <summary>
    /// Loans + generated salary slips. Loans are deducted from the next salary slip
    /// (one-time or by monthly installment); each deduction is recorded so a slip can be
    /// re-generated identically and never deducts the same loan twice.
    /// </summary>
    public sealed partial class AppDb
    {
        private void InitializeLoansAndSalary(SQLiteConnection conn)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS Loan (
    Id           INTEGER PRIMARY KEY AUTOINCREMENT,
    EnrollNumber TEXT NOT NULL,
    Date         TEXT NOT NULL,
    Amount       REAL NOT NULL,
    Installment  REAL NOT NULL DEFAULT 0,   -- 0 = deduct the whole amount at once
    Deducted     REAL NOT NULL DEFAULT 0,
    Remarks      TEXT,
    CreatedAt    TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_Loan_Enroll ON Loan(EnrollNumber);

-- One line per loan deduction on a slip (the slip's Date/Payment/Remarks loan table).
CREATE TABLE IF NOT EXISTS LoanDeduction (
    Id           INTEGER PRIMARY KEY AUTOINCREMENT,
    LoanId       INTEGER NOT NULL,
    EnrollNumber TEXT NOT NULL,
    PeriodKey    TEXT NOT NULL,
    LoanDate     TEXT NOT NULL,
    Amount       REAL NOT NULL,
    Remarks      TEXT,
    DeductedAt   TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_LoanDeduction_Slip ON LoanDeduction(EnrollNumber, PeriodKey);

-- One generated month (the Salary tab list + its scheduled auto-send).
CREATE TABLE IF NOT EXISTS SalaryBatch (
    PeriodKey       TEXT PRIMARY KEY,
    PeriodFrom      TEXT NOT NULL,
    PeriodTo        TEXT NOT NULL,
    GeneratedAt     TEXT NOT NULL,
    ScheduleDate    TEXT,
    ScheduleEnabled INTEGER NOT NULL DEFAULT 0,
    SentAt          TEXT
);

-- Snapshot of one employee's slip for a month (so re-generate reproduces it exactly).
CREATE TABLE IF NOT EXISTS SalarySlip (
    Id                INTEGER PRIMARY KEY AUTOINCREMENT,
    PeriodKey         TEXT NOT NULL,
    EnrollNumber      TEXT NOT NULL,
    Name              TEXT,
    Cnic              TEXT,
    Department        TEXT,
    Designation       TEXT,
    Shift             TEXT,
    WorkingDays       INTEGER,
    Present           INTEGER,
    Absent            INTEGER,
    PaidLeaveDays     INTEGER,
    UnpaidLeaveDays   INTEGER,
    LateCount         INTEGER,
    LateMinutes       INTEGER,
    WorkedHours       REAL,
    OvertimeHours     REAL,
    LateDeductionDays REAL,
    PayableDays       REAL,
    Salary            REAL,
    DailyRate         REAL,
    BasePay           REAL,
    OvertimePay       REAL,
    IncludeOvertime   INTEGER,
    LoanDeduction     REAL,
    NetPay            REAL,
    GeneratedAt       TEXT NOT NULL,
    UNIQUE(PeriodKey, EnrollNumber)
);";
                cmd.ExecuteNonQuery();
            }
        }

        // ---- Loans ------------------------------------------------------------------

        public long InsertLoan(Loan loan)
        {
            using (var conn = Open())
            {
                long id;
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT INTO Loan (EnrollNumber, Date, Amount, Installment, Deducted, Remarks, CreatedAt)
VALUES ($e, $d, $a, $i, 0, $r, $at);
SELECT last_insert_rowid();";
                    cmd.Parameters.AddWithValue("$e", loan.EnrollNumber ?? string.Empty);
                    cmd.Parameters.AddWithValue("$d", loan.Date.ToString(DateFormat));
                    cmd.Parameters.AddWithValue("$a", loan.Amount);
                    cmd.Parameters.AddWithValue("$i", loan.Installment);
                    cmd.Parameters.AddWithValue("$r", (object)loan.Remarks ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$at", DateTime.Now.ToString(TimeFormat));
                    id = Convert.ToInt64(cmd.ExecuteScalar());
                }
                Audit(conn, "Loan added", "Loan", id.ToString(),
                    $"enroll {loan.EnrollNumber} amount {loan.Amount:0.##}" +
                    (loan.Installment > 0 ? $" installment {loan.Installment:0.##}" : " (one-time)"));
                return id;
            }
        }

        public void DeleteLoan(long id)
        {
            using (var conn = Open())
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM Loan WHERE Id=$id;";
                    cmd.Parameters.AddWithValue("$id", id);
                    cmd.ExecuteNonQuery();
                }
                Audit(conn, "Loan removed", "Loan", id.ToString(), null);
            }
        }

        public IReadOnlyList<Loan> GetLoans(string enroll)
        {
            var list = new List<Loan>();
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT Id, EnrollNumber, Date, Amount, Installment, Deducted, Remarks, CreatedAt
FROM Loan WHERE EnrollNumber=$e ORDER BY Date, Id;";
                cmd.Parameters.AddWithValue("$e", enroll ?? string.Empty);
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        list.Add(new Loan
                        {
                            Id = r.GetInt64(0),
                            EnrollNumber = r.GetString(1),
                            Date = ParseDate(r.GetString(2)),
                            Amount = r.GetDouble(3),
                            Installment = r.GetDouble(4),
                            Deducted = r.GetDouble(5),
                            Remarks = r.IsDBNull(6) ? null : r.GetString(6),
                            CreatedAt = ParseTime(r.GetString(7))
                        });
            }
            return list;
        }

        /// <summary>Per-employee loan rollup (all active/inactive employees that have a loan).</summary>
        public IReadOnlyList<LoanSummary> GetLoanSummaries()
        {
            var list = new List<LoanSummary>();
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT e.EnrollNumber, e.Name,
       COALESCE(SUM(l.Amount), 0)            AS Total,
       COALESCE(SUM(l.Amount - l.Deducted),0) AS Outstanding,
       COUNT(l.Id)                            AS Cnt
FROM Employee e
LEFT JOIN Loan l ON l.EnrollNumber = e.EnrollNumber
GROUP BY e.EnrollNumber, e.Name
ORDER BY Outstanding DESC, CAST(e.EnrollNumber AS INTEGER);";
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        list.Add(new LoanSummary
                        {
                            EnrollNumber = r.GetString(0),
                            Name = r.IsDBNull(1) ? null : r.GetString(1),
                            TotalLoan = r.GetDouble(2),
                            Outstanding = r.GetDouble(3),
                            LoanCount = r.GetInt32(4)
                        });
            }
            return list;
        }

        // ---- Salary batch / slip snapshots ------------------------------------------

        public bool SalaryBatchExists(string periodKey)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM SalaryBatch WHERE PeriodKey=$p;";
                cmd.Parameters.AddWithValue("$p", periodKey);
                return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
            }
        }

        /// <summary>
        /// Persists a generated month atomically: the batch, each employee's slip snapshot,
        /// and the loan deductions (bumping each loan's Deducted). Applies loans to the
        /// passed-in slips (sets LoanDeduction, Loans, and reduces NetPay). Returns the slips.
        /// Loans are read and applied INSIDE the transaction so a slip never over-deducts.
        /// </summary>
        public IReadOnlyList<SalarySlip> GenerateAndSaveMonth(
            string periodKey, DateTime from, DateTime to, IList<SalarySlip> baseSlips)
        {
            using (var conn = Open())
            using (var tx = conn.BeginTransaction())
            {
                InsertBatch(conn, tx, periodKey, from, to);

                foreach (var slip in baseSlips)
                {
                    var outstanding = ReadOutstandingLoans(conn, tx, slip.Enroll);
                    var lines = new List<LoanLine>();
                    double loanTotal = 0;

                    foreach (var loan in outstanding)
                    {
                        double take = loan.NextDeduction;
                        if (take <= 0) continue;
                        loanTotal += take;
                        lines.Add(new LoanLine { Date = loan.Date, Amount = take, Remarks = loan.Remarks });

                        InsertLoanDeduction(conn, tx, loan.Id, slip.Enroll, periodKey, loan.Date, take, loan.Remarks);
                        BumpLoanDeducted(conn, tx, loan.Id, take);
                    }

                    slip.LoanDeduction = loanTotal;
                    slip.Loans = lines;
                    slip.NetPay = Math.Max(0, slip.NetPay - loanTotal);

                    InsertSlipSnapshot(conn, tx, periodKey, slip);
                }

                tx.Commit();
            }

            using (var conn = Open())
                Audit(conn, "Salary generated", "SalaryBatch", periodKey,
                    $"{baseSlips.Count} slip(s) for {from:yyyy-MM-dd}..{to:yyyy-MM-dd}");

            return (IReadOnlyList<SalarySlip>)baseSlips;
        }

        private static void InsertBatch(SQLiteConnection conn, SQLiteTransaction tx,
            string periodKey, DateTime from, DateTime to)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT INTO SalaryBatch (PeriodKey, PeriodFrom, PeriodTo, GeneratedAt, ScheduleEnabled)
VALUES ($p, $f, $t, $at, 0);";
                cmd.Parameters.AddWithValue("$p", periodKey);
                cmd.Parameters.AddWithValue("$f", from.ToString(DateFormat));
                cmd.Parameters.AddWithValue("$t", to.ToString(DateFormat));
                cmd.Parameters.AddWithValue("$at", DateTime.Now.ToString(TimeFormat));
                cmd.ExecuteNonQuery();
            }
        }

        private List<Loan> ReadOutstandingLoans(SQLiteConnection conn, SQLiteTransaction tx, string enroll)
        {
            var list = new List<Loan>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
SELECT Id, EnrollNumber, Date, Amount, Installment, Deducted, Remarks
FROM Loan WHERE EnrollNumber=$e AND (Amount - Deducted) > 0.0001 ORDER BY Date, Id;";
                cmd.Parameters.AddWithValue("$e", enroll ?? string.Empty);
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        list.Add(new Loan
                        {
                            Id = r.GetInt64(0),
                            EnrollNumber = r.GetString(1),
                            Date = ParseDate(r.GetString(2)),
                            Amount = r.GetDouble(3),
                            Installment = r.GetDouble(4),
                            Deducted = r.GetDouble(5),
                            Remarks = r.IsDBNull(6) ? null : r.GetString(6)
                        });
            }
            return list;
        }

        private static void InsertLoanDeduction(SQLiteConnection conn, SQLiteTransaction tx,
            long loanId, string enroll, string periodKey, DateTime loanDate, double amount, string remarks)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT INTO LoanDeduction (LoanId, EnrollNumber, PeriodKey, LoanDate, Amount, Remarks, DeductedAt)
VALUES ($l, $e, $p, $d, $a, $r, $at);";
                cmd.Parameters.AddWithValue("$l", loanId);
                cmd.Parameters.AddWithValue("$e", enroll ?? string.Empty);
                cmd.Parameters.AddWithValue("$p", periodKey);
                cmd.Parameters.AddWithValue("$d", loanDate.ToString(DateFormat));
                cmd.Parameters.AddWithValue("$a", amount);
                cmd.Parameters.AddWithValue("$r", (object)remarks ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$at", DateTime.Now.ToString(TimeFormat));
                cmd.ExecuteNonQuery();
            }
        }

        private static void BumpLoanDeducted(SQLiteConnection conn, SQLiteTransaction tx, long loanId, double amount)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE Loan SET Deducted = Deducted + $a WHERE Id=$id;";
                cmd.Parameters.AddWithValue("$a", amount);
                cmd.Parameters.AddWithValue("$id", loanId);
                cmd.ExecuteNonQuery();
            }
        }

        private static void InsertSlipSnapshot(SQLiteConnection conn, SQLiteTransaction tx, string periodKey, SalarySlip s)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT INTO SalarySlip
 (PeriodKey, EnrollNumber, Name, Cnic, Department, Designation, Shift,
  WorkingDays, Present, Absent, PaidLeaveDays, UnpaidLeaveDays, LateCount, LateMinutes,
  WorkedHours, OvertimeHours, LateDeductionDays, PayableDays,
  Salary, DailyRate, BasePay, OvertimePay, IncludeOvertime, LoanDeduction, NetPay, GeneratedAt)
VALUES
 ($p, $e, $name, $cnic, $dep, $des, $shift,
  $wd, $pr, $ab, $pl, $ul, $lc, $lm,
  $wh, $ot, $ldd, $pd,
  $sal, $dr, $bp, $op, $io, $loan, $net, $at);";
                cmd.Parameters.AddWithValue("$p", periodKey);
                cmd.Parameters.AddWithValue("$e", s.Enroll ?? string.Empty);
                cmd.Parameters.AddWithValue("$name", (object)s.Name ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$cnic", (object)s.Cnic ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$dep", (object)s.Department ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$des", (object)s.Designation ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$shift", (object)s.Shift ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$wd", s.WorkingDays);
                cmd.Parameters.AddWithValue("$pr", s.Present);
                cmd.Parameters.AddWithValue("$ab", s.Absent);
                cmd.Parameters.AddWithValue("$pl", s.PaidLeaveDays);
                cmd.Parameters.AddWithValue("$ul", s.UnpaidLeaveDays);
                cmd.Parameters.AddWithValue("$lc", s.LateCount);
                cmd.Parameters.AddWithValue("$lm", s.LateMinutes);
                cmd.Parameters.AddWithValue("$wh", s.WorkedHours);
                cmd.Parameters.AddWithValue("$ot", s.OvertimeHours);
                cmd.Parameters.AddWithValue("$ldd", s.LateDeductionDays);
                cmd.Parameters.AddWithValue("$pd", s.PayableDays);
                cmd.Parameters.AddWithValue("$sal", s.Salary);
                cmd.Parameters.AddWithValue("$dr", s.DailyRate);
                cmd.Parameters.AddWithValue("$bp", s.BasePay);
                cmd.Parameters.AddWithValue("$op", s.OvertimePay);
                cmd.Parameters.AddWithValue("$io", s.IncludeOvertime ? 1 : 0);
                cmd.Parameters.AddWithValue("$loan", s.LoanDeduction);
                cmd.Parameters.AddWithValue("$net", s.NetPay);
                cmd.Parameters.AddWithValue("$at", DateTime.Now.ToString(TimeFormat));
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>Reconstructs the stored slips for a month (with their loan lines) — used to re-generate/send.</summary>
        public IReadOnlyList<SalarySlip> GetSlipSnapshots(string periodKey)
        {
            var slips = new List<SalarySlip>();
            using (var conn = Open())
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
SELECT EnrollNumber, Name, Cnic, Department, Designation, Shift,
       WorkingDays, Present, Absent, PaidLeaveDays, UnpaidLeaveDays, LateCount, LateMinutes,
       WorkedHours, OvertimeHours, LateDeductionDays, PayableDays,
       Salary, DailyRate, BasePay, OvertimePay, IncludeOvertime, LoanDeduction, NetPay
FROM SalarySlip WHERE PeriodKey=$p ORDER BY CAST(EnrollNumber AS INTEGER);";
                    cmd.Parameters.AddWithValue("$p", periodKey);
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                            slips.Add(new SalarySlip
                            {
                                Enroll = r.GetString(0),
                                Name = r.IsDBNull(1) ? null : r.GetString(1),
                                Cnic = r.IsDBNull(2) ? null : r.GetString(2),
                                Department = r.IsDBNull(3) ? null : r.GetString(3),
                                Designation = r.IsDBNull(4) ? null : r.GetString(4),
                                Shift = r.IsDBNull(5) ? null : r.GetString(5),
                                WorkingDays = r.GetInt32(6),
                                Present = r.GetInt32(7),
                                Absent = r.GetInt32(8),
                                PaidLeaveDays = r.GetInt32(9),
                                UnpaidLeaveDays = r.GetInt32(10),
                                LateCount = r.GetInt32(11),
                                LateMinutes = r.GetInt32(12),
                                WorkedHours = r.GetDouble(13),
                                OvertimeHours = r.GetDouble(14),
                                LateDeductionDays = r.GetDouble(15),
                                PayableDays = r.GetDouble(16),
                                Salary = r.GetDouble(17),
                                DailyRate = r.GetDouble(18),
                                BasePay = r.GetDouble(19),
                                OvertimePay = r.GetDouble(20),
                                IncludeOvertime = r.GetInt32(21) != 0,
                                LoanDeduction = r.GetDouble(22),
                                NetPay = r.GetDouble(23),
                                Loans = new List<LoanLine>()
                            });
                }

                // Attach loan lines per employee.
                foreach (var s in slips)
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = @"
SELECT LoanDate, Amount, Remarks FROM LoanDeduction
WHERE PeriodKey=$p AND EnrollNumber=$e ORDER BY LoanDate, Id;";
                        cmd.Parameters.AddWithValue("$p", periodKey);
                        cmd.Parameters.AddWithValue("$e", s.Enroll);
                        using (var r = cmd.ExecuteReader())
                            while (r.Read())
                                s.Loans.Add(new LoanLine
                                {
                                    Date = ParseDate(r.GetString(0)),
                                    Amount = r.GetDouble(1),
                                    Remarks = r.IsDBNull(2) ? null : r.GetString(2)
                                });
                    }
                }
            }
            return slips;
        }

        public IReadOnlyList<SalaryBatch> GetSalaryBatches()
        {
            var list = new List<SalaryBatch>();
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT PeriodKey, PeriodFrom, PeriodTo, GeneratedAt, ScheduleDate, ScheduleEnabled, SentAt
FROM SalaryBatch ORDER BY PeriodFrom DESC;";
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        list.Add(ReadBatch(r));
            }
            return list;
        }

        public SalaryBatch GetSalaryBatch(string periodKey)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT PeriodKey, PeriodFrom, PeriodTo, GeneratedAt, ScheduleDate, ScheduleEnabled, SentAt
FROM SalaryBatch WHERE PeriodKey=$p;";
                cmd.Parameters.AddWithValue("$p", periodKey);
                using (var r = cmd.ExecuteReader())
                    return r.Read() ? ReadBatch(r) : null;
            }
        }

        private static SalaryBatch ReadBatch(SQLiteDataReader r) => new SalaryBatch
        {
            PeriodKey = r.GetString(0),
            PeriodFrom = ParseDate(r.GetString(1)),
            PeriodTo = ParseDate(r.GetString(2)),
            GeneratedAt = ParseTime(r.GetString(3)),
            ScheduleDate = r.IsDBNull(4) ? (DateTime?)null : ParseDate(r.GetString(4)),
            ScheduleEnabled = r.GetInt32(5) != 0,
            SentAt = r.IsDBNull(6) ? (DateTime?)null : ParseTime(r.GetString(6))
        };

        public void SetBatchSchedule(string periodKey, DateTime? date, bool enabled)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "UPDATE SalaryBatch SET ScheduleDate=$d, ScheduleEnabled=$en WHERE PeriodKey=$p;";
                cmd.Parameters.AddWithValue("$d", date.HasValue ? (object)date.Value.ToString(DateFormat) : DBNull.Value);
                cmd.Parameters.AddWithValue("$en", enabled ? 1 : 0);
                cmd.Parameters.AddWithValue("$p", periodKey);
                cmd.ExecuteNonQuery();
            }
        }

        public void MarkBatchSent(string periodKey, DateTime at)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "UPDATE SalaryBatch SET SentAt=$at WHERE PeriodKey=$p;";
                cmd.Parameters.AddWithValue("$at", at.ToString(TimeFormat));
                cmd.Parameters.AddWithValue("$p", periodKey);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>Enabled, not-yet-sent batches whose scheduled date is on or before today.</summary>
        public IReadOnlyList<SalaryBatch> GetDueScheduledBatches(DateTime today)
        {
            var list = new List<SalaryBatch>();
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT PeriodKey, PeriodFrom, PeriodTo, GeneratedAt, ScheduleDate, ScheduleEnabled, SentAt
FROM SalaryBatch
WHERE ScheduleEnabled=1 AND SentAt IS NULL AND ScheduleDate IS NOT NULL AND ScheduleDate <= $today;";
                cmd.Parameters.AddWithValue("$today", today.ToString(DateFormat));
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        list.Add(ReadBatch(r));
            }
            return list;
        }

        private static DateTime ParseDate(string s)
            => DateTime.TryParseExact(s, DateFormat, null,
                System.Globalization.DateTimeStyles.None, out var d)
                ? d
                : (DateTime.TryParse(s, out var f) ? f : default);
    }
}
