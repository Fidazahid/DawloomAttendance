using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DawloomAttendance.Data;
using DawloomAttendance.Services;

namespace DawloomAttendance.Views
{
    /// <summary>Date-range reports (summary / detail / late / absent) with Excel export.</summary>
    public partial class ReportsView : UserControl
    {
        private readonly AppDb _db;
        private DataTable _current;
        private string _reportName = "Report";

        public ReportsView(AppDb db)
        {
            InitializeComponent();
            _db = db;
            // PeriodCombo defaults to "This month" (pickers disabled in XAML).
            var today = DateTime.Today;
            FromPick.SelectedDate = new DateTime(today.Year, today.Month, 1);
            ToPick.SelectedDate = today;

            var emps = new List<EmpFilter> { new EmpFilter { Enroll = null, Display = "All employees" } };
            emps.AddRange(_db.GetEmployees().Select(e => new EmpFilter { Enroll = e.EnrollNumber, Display = $"{e.EnrollNumber} - {e.Name}" }));
            EmployeeFilter.ItemsSource = emps;
            EmployeeFilter.SelectedIndex = 0;
        }

        private class EmpFilter
        {
            public string Enroll { get; set; }
            public string Display { get; set; }
        }

        private string SelectedType => (TypeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Monthly summary";

        private void PeriodCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (FromPick == null || ToPick == null) return;   // fires during InitializeComponent
            var today = DateTime.Today;
            string sel = (PeriodCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "This month";

            if (sel == "This month")
            {
                FromPick.SelectedDate = new DateTime(today.Year, today.Month, 1);
                ToPick.SelectedDate = today;
                FromPick.IsEnabled = ToPick.IsEnabled = false;
            }
            else if (sel == "Last month")
            {
                var firstThis = new DateTime(today.Year, today.Month, 1);
                FromPick.SelectedDate = firstThis.AddMonths(-1);
                ToPick.SelectedDate = firstThis.AddDays(-1);
                FromPick.IsEnabled = ToPick.IsEnabled = false;
            }
            else // Custom
            {
                FromPick.IsEnabled = ToPick.IsEnabled = true;
            }
        }

        private void RunButton_Click(object sender, RoutedEventArgs e)
        {
            var from = FromPick.SelectedDate ?? DateTime.Today;
            var to = ToPick.SelectedDate ?? DateTime.Today;
            if (to < from) { MessageBox.Show(Window.GetWindow(this), "‘To’ must be on or after ‘From’.", "Reports", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

            var names = _db.GetEmployees().ToDictionary(x => x.EnrollNumber);
            var shifts = _db.GetShifts().ToDictionary(s => s.Id);
            var shiftByEnroll = new Dictionary<string, Data.Entities.Shift>();
            foreach (var emp in names.Values)
                shiftByEnroll[emp.EnrollNumber] =
                    (emp.ShiftId.HasValue && shifts.TryGetValue(emp.ShiftId.Value, out var sh)) ? sh : null;

            var data = new AttendanceService(_db).ComputeForRange(from, to);

            // Optional single-employee filter.
            var selEnroll = EmployeeFilter.SelectedValue as string;
            if (!string.IsNullOrEmpty(selEnroll))
                data = data.Where(d => d.EnrollNumber == selEnroll).ToList();

            switch (SelectedType)
            {
                case "Daily detail": _current = DailyDetail(data, names, shiftByEnroll); _reportName = "Daily detail"; break;
                case "Late arrivals": _current = LateList(data, names); _reportName = "Late arrivals"; break;
                case "Absentees": _current = AbsentList(data, names); _reportName = "Absentees"; break;
                default: _current = MonthlySummary(data, names, shiftByEnroll); _reportName = "Summary"; break;
            }

            Grid.ItemsSource = _current.DefaultView;
            Summary.Text = $"{_reportName}: {from:yyyy-MM-dd} to {to:yyyy-MM-dd} — {_current.Rows.Count} rows.";
            ExportButton.IsEnabled = _current.Rows.Count > 0;
        }

        private static string Nm(IDictionary<string, Data.Entities.Employee> names, string enroll)
            => names.TryGetValue(enroll, out var emp) ? emp.Name : "";
        private static string Dept(IDictionary<string, Data.Entities.Employee> names, string enroll)
            => names.TryGetValue(enroll, out var emp) ? emp.Department : "";

        private static DataTable MonthlySummary(List<DailyAttendance> data, IDictionary<string, Data.Entities.Employee> names,
            IDictionary<string, Data.Entities.Shift> shiftByEnroll)
        {
            var t = new DataTable();
            t.Columns.Add("Enroll"); t.Columns.Add("Name"); t.Columns.Add("Department"); t.Columns.Add("Shift");
            t.Columns.Add("Working days"); t.Columns.Add("Present"); t.Columns.Add("Absent");
            t.Columns.Add("Late days"); t.Columns.Add("Late time"); t.Columns.Add("Worked"); t.Columns.Add("Attendance %");

            foreach (var g in data.GroupBy(d => d.EnrollNumber).OrderBy(g => SortKey(g.Key)))
            {
                shiftByEnroll.TryGetValue(g.Key, out var shift);
                double shiftHours = ShiftHours(shift);

                int workingDays = g.Count(x => x.IsWorkingDay);
                double workedTotal = g.Sum(x => x.WorkedHours);                              // all days (incl. any holiday work)
                double workedOnWorkingDays = g.Where(x => x.IsWorkingDay).Sum(x => x.WorkedHours);
                int lateMinutes = g.Where(x => x.Late).Sum(x => x.LateMinutes);
                double expected = workingDays * shiftHours;                                  // per the employee's shift; holidays excluded
                string pct = shiftHours > 0 && expected > 0
                    ? (workedOnWorkingDays / expected * 100).ToString("0.#") + "%"
                    : "—";

                t.Rows.Add(
                    g.Key, Nm(names, g.Key), Dept(names, g.Key), ShiftDisplay(shift),
                    workingDays,
                    g.Count(x => x.Present),
                    g.Count(x => x.Absent),
                    g.Count(x => x.Late),
                    DurationFormat.Minutes(lateMinutes),
                    DurationFormat.Hours(workedTotal),
                    pct);
            }
            return t;
        }

        private static DataTable DailyDetail(List<DailyAttendance> data, IDictionary<string, Data.Entities.Employee> names,
            IDictionary<string, Data.Entities.Shift> shiftByEnroll)
        {
            var t = new DataTable();
            t.Columns.Add("Date"); t.Columns.Add("Enroll"); t.Columns.Add("Name"); t.Columns.Add("Shift");
            t.Columns.Add("Status"); t.Columns.Add("Check In"); t.Columns.Add("Check Out");
            t.Columns.Add("Late"); t.Columns.Add("Worked");
            foreach (var d in data.OrderBy(x => x.Date).ThenBy(x => SortKey(x.EnrollNumber)))
            {
                shiftByEnroll.TryGetValue(d.EnrollNumber, out var shift);
                t.Rows.Add(d.Date.ToString("yyyy-MM-dd"), d.EnrollNumber, Nm(names, d.EnrollNumber), ShiftDisplay(shift), d.Status,
                    d.FirstPunch?.ToString("HH:mm:ss") ?? "", d.LastPunch?.ToString("HH:mm:ss") ?? "",
                    d.Late ? DurationFormat.Minutes(d.LateMinutes) : "", DurationFormat.Hours(d.WorkedHours));
            }
            return t;
        }

        private static double ShiftHours(Data.Entities.Shift s)
        {
            if (s == null) return 0;
            if (TimeSpan.TryParseExact(s.StartTime, @"hh\:mm", CultureInfo.InvariantCulture, out var st) &&
                TimeSpan.TryParseExact(s.EndTime, @"hh\:mm", CultureInfo.InvariantCulture, out var en))
            {
                double h = (en - st).TotalHours;
                if (h <= 0) h += 24; // overnight shift
                return h;
            }
            return 0;
        }

        private static string ShiftDisplay(Data.Entities.Shift s)
            => s == null ? "—" : $"{s.Name} ({s.StartTime}-{s.EndTime})";

        private static DataTable LateList(List<DailyAttendance> data, IDictionary<string, Data.Entities.Employee> names)
        {
            var t = new DataTable();
            t.Columns.Add("Date"); t.Columns.Add("Enroll"); t.Columns.Add("Name"); t.Columns.Add("Check In"); t.Columns.Add("Late");
            foreach (var d in data.Where(x => x.Late).OrderBy(x => x.Date).ThenBy(x => SortKey(x.EnrollNumber)))
                t.Rows.Add(d.Date.ToString("yyyy-MM-dd"), d.EnrollNumber, Nm(names, d.EnrollNumber),
                    d.FirstPunch?.ToString("HH:mm:ss") ?? "", DurationFormat.Minutes(d.LateMinutes));
            return t;
        }

        private static DataTable AbsentList(List<DailyAttendance> data, IDictionary<string, Data.Entities.Employee> names)
        {
            var t = new DataTable();
            t.Columns.Add("Date"); t.Columns.Add("Enroll"); t.Columns.Add("Name");
            foreach (var d in data.Where(x => x.Absent).OrderBy(x => x.Date).ThenBy(x => SortKey(x.EnrollNumber)))
                t.Rows.Add(d.Date.ToString("yyyy-MM-dd"), d.EnrollNumber, Nm(names, d.EnrollNumber));
            return t;
        }

        private static int SortKey(string enroll) => int.TryParse(enroll, out var n) ? n : int.MaxValue;

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null || _current.Rows.Count == 0) return;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                FileName = $"Dawloom_{_reportName.Replace(' ', '_')}_{DateTime.Now:yyyyMMdd}.xlsx"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var headers = _current.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToList();
                var rows = _current.Rows.Cast<DataRow>()
                    .Select(r => (IList<string>)r.ItemArray.Select(v => v?.ToString() ?? "").ToList());
                ExcelExport.Write(dlg.FileName, _reportName, headers, rows);
                MessageBox.Show(Window.GetWindow(this), "Saved: " + dlg.FileName, "Export", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), "Export failed: " + ex.Message, "Export", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
