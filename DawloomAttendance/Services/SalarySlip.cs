namespace DawloomAttendance.Services
{
    /// <summary>One employee's computed payroll figures for a period (drives the slip + payroll table).</summary>
    public class SalarySlip
    {
        public string Enroll { get; set; }
        public string Name { get; set; }
        public string Cnic { get; set; }
        public string Department { get; set; }
        public string Designation { get; set; }
        public string Shift { get; set; }

        public int WorkingDays { get; set; }
        public int Present { get; set; }
        public int Absent { get; set; }
        public int LeaveDays { get; set; }
        public int LateCount { get; set; }
        public int LateMinutes { get; set; }
        public double WorkedHours { get; set; }
        public double OvertimeHours { get; set; }

        public double LateDeductionDays { get; set; }
        public double PayableDays { get; set; }

        public double Salary { get; set; }
        public double DailyRate { get; set; }
        public double BasePay { get; set; }
        public double OvertimePay { get; set; }
        public bool IncludeOvertime { get; set; }
        public double NetPay { get; set; }
    }
}
