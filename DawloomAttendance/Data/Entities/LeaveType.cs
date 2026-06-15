namespace DawloomAttendance.Data.Entities
{
    /// <summary>
    /// A category of leave (Annual, Sick, Casual, Unpaid, …). <see cref="Paid"/> decides
    /// whether days taken under this type still count toward pay; <see cref="DefaultDays"/>
    /// is the yearly entitlement granted to an employee when no per-employee override exists.
    /// </summary>
    public class LeaveType
    {
        public long Id { get; set; }

        /// <summary>Stable lowercase key (annual/sick/casual/unpaid) — unique, used in code.</summary>
        public string Code { get; set; }

        /// <summary>Human-friendly name shown in the UI.</summary>
        public string Name { get; set; }

        /// <summary>True when a day on this leave is still paid (counts toward payable days).</summary>
        public bool Paid { get; set; } = true;

        /// <summary>Default yearly entitlement (days) when the employee has no override.</summary>
        public double DefaultDays { get; set; }

        public bool Active { get; set; } = true;
    }
}
