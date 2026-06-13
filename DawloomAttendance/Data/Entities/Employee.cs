using System;

namespace DawloomAttendance.Data.Entities
{
    /// <summary>
    /// An employee's HR profile, keyed to their device enrollment number so raw
    /// punches (which only carry an EnrollNumber) can be resolved to a real person.
    /// </summary>
    public class Employee
    {
        public long Id { get; set; }

        /// <summary>Device enrollment / user ID (the link to RawPunch.EnrollNumber).</summary>
        public string EnrollNumber { get; set; }

        public string Name { get; set; }
        public string Cnic { get; set; }
        public string Department { get; set; }
        public string Designation { get; set; }
        public string Contact { get; set; }

        /// <summary>Monthly gross salary, used by the payroll report to compute pay.</summary>
        public double Salary { get; set; }

        /// <summary>Assigned shift (FK to Shift); null until shifts are introduced.</summary>
        public long? ShiftId { get; set; }

        public bool Active { get; set; } = true;
        public DateTime CreatedAt { get; set; }
    }
}
