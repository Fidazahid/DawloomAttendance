using System;

namespace DawloomAttendance.Data.Entities
{
    /// <summary>
    /// One day of leave taken by an employee under a specific <see cref="LeaveType"/>.
    /// A multi-day request is stored as one row per calendar day so it slots straight
    /// into the day-by-day attendance model and makes balance math a simple count.
    /// </summary>
    public class LeaveEntry
    {
        public long Id { get; set; }
        public string EnrollNumber { get; set; }
        public long LeaveTypeId { get; set; }
        public DateTime Date { get; set; } = DateTime.Today;
        public string Reason { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// A computed leave-balance row for one employee/type/year (not persisted): the
    /// granted entitlement, how many days were taken, and what remains.
    /// </summary>
    public class LeaveBalance
    {
        public long LeaveTypeId { get; set; }
        public string TypeCode { get; set; }
        public string TypeName { get; set; }
        public bool Paid { get; set; }
        public int Year { get; set; }
        public double Entitled { get; set; }
        public double Taken { get; set; }
        public double Remaining => Entitled - Taken;
    }
}
