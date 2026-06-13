using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DawloomAttendance.Data;
using DawloomAttendance.Device;
using DawloomAttendance.Device.Events;
using DawloomAttendance.Services;

namespace DawloomAttendance.Views
{
    /// <summary>
    /// "Today at a glance": tiles, an employee status grid with search/filter, and a
    /// live recent-activity feed that updates as punches arrive.
    /// </summary>
    public partial class DashboardWindow : System.Windows.Controls.UserControl
    {
        private readonly AppDb _db;
        private readonly IZkDevice _device;
        private List<DashboardRow> _all = new List<DashboardRow>();
        private static readonly HashSet<int> OutStates = new HashSet<int> { 1, 5 }; // Check-Out, Overtime-Out

        public DashboardWindow(AppDb db, IZkDevice device)
        {
            InitializeComponent();
            _db = db;
            _device = device;
            DatePick.SelectedDate = DateTime.Today;

            _device.PunchReceived += OnPunch;
            Unloaded += (_, __) => _device.PunchReceived -= OnPunch;

            Refresh();
        }

        private DateTime SelectedDate => DatePick.SelectedDate ?? DateTime.Today;

        private void RefreshButton_Click(object sender, RoutedEventArgs e) => Refresh();
        private void DatePick_Changed(object sender, SelectionChangedEventArgs e) => Refresh();

        private void EditPunchesButton_Click(object sender, RoutedEventArgs e)
        {
            if (!(Grid.SelectedItem is DashboardRow row))
            {
                MessageBox.Show(Window.GetWindow(this), "Select an employee row first.", "Edit Punches", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var win = new PunchEditWindow(_db, row.Enroll, SelectedDate, row.Name) { Owner = Window.GetWindow(this) };
            win.ShowDialog();
            if (win.Changed) Refresh();
        }
        private void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

        private void Refresh()
        {
            if (_db == null) return;
            var date = SelectedDate;
            var emps = _db.GetEmployees().ToDictionary(x => x.EnrollNumber);
            var results = new AttendanceService(_db).ComputeForDate(date);

            // Last punch's in/out state per employee → "in office".
            var lastState = _db.GetPunchesForDate(date)
                .GroupBy(p => p.EnrollNumber)
                .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Timestamp).Last().AttState);

            _all = results.Select(r =>
            {
                emps.TryGetValue(r.EnrollNumber, out var emp);
                bool inOffice = r.Present &&
                    !(lastState.TryGetValue(r.EnrollNumber, out var st) && OutStates.Contains(st));
                return new DashboardRow
                {
                    Enroll = r.EnrollNumber,
                    Name = emp?.Name,
                    Dept = emp?.Department,
                    Status = r.Status,
                    In = r.FirstPunch?.ToString("HH:mm:ss") ?? "",
                    Out = r.LastPunch?.ToString("HH:mm:ss") ?? "",
                    Late = r.Late ? DurationFormat.Minutes(r.LateMinutes) : "",
                    Worked = DurationFormat.Hours(r.WorkedHours),
                    Present = r.Present,
                    Absent = r.Absent,
                    IsLate = r.Late,
                    InOffice = inOffice
                };
            }).ToList();

            TotalCount.Text = _all.Count.ToString();
            PresentCount.Text = _all.Count(x => x.Present).ToString();
            AbsentCount.Text = _all.Count(x => x.Absent).ToString();
            LateCount.Text = _all.Count(x => x.IsLate).ToString();
            InOfficeCount.Text = _all.Count(x => x.InOffice).ToString();

            ApplyFilter();
        }

        private void ApplyFilter()
        {
            // The filter ComboBox's IsSelected fires SelectionChanged during
            // InitializeComponent, before Grid/SearchBox exist — ignore until ready.
            if (Grid == null || SearchBox == null || _all == null) return;
            string q = (SearchBox.Text ?? "").Trim().ToLowerInvariant();
            string filter = (FilterCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All";

            IEnumerable<DashboardRow> rows = _all;
            switch (filter)
            {
                case "Present": rows = rows.Where(r => r.Present); break;
                case "Absent": rows = rows.Where(r => r.Absent); break;
                case "Late": rows = rows.Where(r => r.IsLate); break;
                case "In Office": rows = rows.Where(r => r.InOffice); break;
            }
            if (q.Length > 0)
                rows = rows.Where(r =>
                    (r.Name ?? "").ToLowerInvariant().Contains(q) ||
                    (r.Enroll ?? "").ToLowerInvariant().Contains(q) ||
                    (r.Dept ?? "").ToLowerInvariant().Contains(q));

            Grid.ItemsSource = rows.ToList();
        }

        // Live punch → recompute today's numbers/tiles.
        private void OnPunch(PunchEvent punch)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (SelectedDate.Date == DateTime.Today) Refresh();
            });
        }
    }

    public class DashboardRow
    {
        public string Enroll { get; set; }
        public string Name { get; set; }
        public string Dept { get; set; }
        public string Status { get; set; }
        public string In { get; set; }
        public string Out { get; set; }
        public string Late { get; set; }
        public string Worked { get; set; }
        public bool Present { get; set; }
        public bool Absent { get; set; }
        public bool IsLate { get; set; }
        public bool InOffice { get; set; }
    }
}
