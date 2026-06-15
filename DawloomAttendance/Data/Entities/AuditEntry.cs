using System;

namespace DawloomAttendance.Data.Entities
{
    /// <summary>
    /// One recorded change to the data: who (<see cref="Actor"/>), when, what
    /// (<see cref="Action"/>), and on which row (<see cref="Entity"/>/<see cref="EntityId"/>).
    /// </summary>
    public class AuditEntry
    {
        public long Id { get; set; }
        public DateTime Timestamp { get; set; }
        public string Actor { get; set; }
        public string Action { get; set; }
        public string Entity { get; set; }
        public string EntityId { get; set; }
        public string Detail { get; set; }
    }
}
