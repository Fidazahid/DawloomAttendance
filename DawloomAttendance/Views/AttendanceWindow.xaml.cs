using System;
using System.Linq;
using System.Windows;
using DawloomAttendance.Data;
using DawloomAttendance.Services;

namespace DawloomAttendance.Views
{
    /// <summary>Shows computed daily attendance for a chosen date (Phase 3 engine over the DB).</summary>
    public partial class AttendanceWindow : Window
    {
        private readonly AppDb _db;

        public AttendanceWindow(AppDb db)
        {
            InitializeComponent();
            _db = db;
            DatePick.SelectedDate = DateTime.Today;
            Compute();
        }

        private void ComputeButton_Click(object sender, RoutedEventArgs e) => Compute();

        private void EditPunchesButton_Click(object sender, RoutedEventArgs e)
        {
            if (!(Grid.SelectedItem is AttendanceRow row))
            {
                MessageBox.Show(this, "Select an employee row first.", "Edit Punches", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var date = DatePick.SelectedDate ?? DateTime.Today;
            var win = new PunchEditWindow(_db, row.Enroll, date, row.Name) { Owner = this };
            win.ShowDialog();
            if (win.Changed) Compute();   // reflect edits/deletes
        }

        private void Compute()
        {
            var date = DatePick.SelectedDate ?? DateTime.Today;
            var names = _db.GetEmployees().ToDictionary(emp => emp.EnrollNumber, emp => emp.Name);
            var results = new AttendanceService(_db).ComputeForDate(date);

            Grid.ItemsSource = results.Select(r => new AttendanceRow
            {
                Enroll = r.EnrollNumber,
                Name = names.TryGetValue(r.EnrollNumber, out var n) ? n : "",
                Status = r.Status,
                First = r.FirstPunch?.ToString("HH:mm:ss") ?? "",
                Last = r.LastPunch?.ToString("HH:mm:ss") ?? "",
                Late = r.Late ? r.LateMinutes.ToString() : "",
                Early = r.EarlyDeparture ? r.EarlyMinutes.ToString() : "",
                Worked = r.WorkedHours > 0 ? r.WorkedHours.ToString("0.##") : "",
                OT = r.OvertimeHours > 0 ? r.OvertimeHours.ToString("0.##") : "",
                Notes = r.Notes
            }).ToList();

            int present = results.Count(x => x.Present);
            int absent = results.Count(x => x.Absent);
            int late = results.Count(x => x.Late);
            Summary.Text = results.Count == 0
                ? $"{date:yyyy-MM-dd}: no employees — import them in the Employees window first."
                : $"{date:yyyy-MM-dd}: {results.Count} employees — {present} present, {absent} absent, {late} late.";
        }
    }

    public class AttendanceRow
    {
        public string Enroll { get; set; }
        public string Name { get; set; }
        public string Status { get; set; }
        public string First { get; set; }
        public string Last { get; set; }
        public string Late { get; set; }
        public string Early { get; set; }
        public string Worked { get; set; }
        public string OT { get; set; }
        public string Notes { get; set; }
    }
}
