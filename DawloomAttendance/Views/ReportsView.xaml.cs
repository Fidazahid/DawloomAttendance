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
        private string _periodText = "";

        public ReportsView(AppDb db)
        {
            InitializeComponent();
            _db = db;
            // PeriodCombo defaults to "This month" (pickers disabled in XAML).
            var today = DateTime.Today;
            FromPick.SelectedDate = new DateTime(today.Year, today.Month, 1);
            ToPick.SelectedDate = today;

            EmployeeList.ItemsSource = _db.GetEmployees()
                .Select(e => new EmpPick { Enroll = e.EnrollNumber, Display = $"{e.EnrollNumber} - {e.Name}" })
                .ToList();

            var lpd = _db.GetSetting("LatesPerDeduction");
            if (!string.IsNullOrWhiteSpace(lpd)) LatesPerDeductionBox.Text = lpd;
        }

        private class EmpPick
        {
            public string Enroll { get; set; }
            public string Display { get; set; }
            public bool IsChecked { get; set; }
        }

        private string SelectedType => (TypeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Monthly summary";

        // Update the dropdown button text when the employee popup closes.
        private void EmpDropdown_Closed(object sender, RoutedEventArgs e)
        {
            int n = (EmployeeList.ItemsSource as IEnumerable<EmpPick>)?.Count(p => p.IsChecked) ?? 0;
            EmpSummaryText.Text = n == 0 ? "All employees" : (n == 1 ? "1 selected" : n + " selected");
        }

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

            // Optional multi-employee filter: none checked = all; some checked = only those.
            var selected = (EmployeeList.ItemsSource as IEnumerable<EmpPick>)
                .Where(p => p.IsChecked).Select(p => p.Enroll).ToList();
            if (selected.Count > 0)
            {
                var set = new HashSet<string>(selected);
                data = data.Where(d => set.Contains(d.EnrollNumber)).ToList();
            }

            if (!int.TryParse(LatesPerDeductionBox.Text, out var latesPerDed) || latesPerDed < 1) latesPerDed = 3;
            _db.SetSetting("LatesPerDeduction", latesPerDed.ToString());

            switch (SelectedType)
            {
                case "Payroll (monthly)": _current = Payroll(data, names, shiftByEnroll, latesPerDed, IncludeOtBox.IsChecked == true); _reportName = "Payroll"; break;
                case "Daily detail": _current = DailyDetail(data, names, shiftByEnroll); _reportName = "Daily detail"; break;
                case "Late arrivals": _current = LateList(data, names); _reportName = "Late arrivals"; break;
                case "Absentees": _current = AbsentList(data, names); _reportName = "Absentees"; break;
                default: _current = MonthlySummary(data, names, shiftByEnroll); _reportName = "Summary"; break;
            }

            Grid.ItemsSource = _current.DefaultView;
            Summary.Text = $"{_reportName}: {from:yyyy-MM-dd} to {to:yyyy-MM-dd} — {_current.Rows.Count} rows.";
            ExportButton.IsEnabled = ExportPdfButton.IsEnabled = _current.Rows.Count > 0;
            _periodText = $"{from:yyyy-MM-dd} to {to:yyyy-MM-dd}";
        }

        private static string Nm(IDictionary<string, Data.Entities.Employee> names, string enroll)
            => names.TryGetValue(enroll, out var emp) ? emp.Name : "";
        private static string Dept(IDictionary<string, Data.Entities.Employee> names, string enroll)
            => names.TryGetValue(enroll, out var emp) ? emp.Department : "";
        private static string Cnic(IDictionary<string, Data.Entities.Employee> names, string enroll)
            => names.TryGetValue(enroll, out var emp) ? emp.Cnic : "";
        private static string Desig(IDictionary<string, Data.Entities.Employee> names, string enroll)
            => names.TryGetValue(enroll, out var emp) ? emp.Designation : "";

        // Payroll-ready monthly metrics per employee (feed into a payroll system).
        private static DataTable Payroll(List<DailyAttendance> data, IDictionary<string, Data.Entities.Employee> names,
            IDictionary<string, Data.Entities.Shift> shiftByEnroll, int latesPerDeduction, bool includeOvertime)
        {
            var t = new DataTable();
            t.Columns.Add("Enroll"); t.Columns.Add("Name"); t.Columns.Add("CNIC");
            t.Columns.Add("Department"); t.Columns.Add("Designation"); t.Columns.Add("Shift");
            t.Columns.Add("Working days"); t.Columns.Add("Present"); t.Columns.Add("Absent");
            t.Columns.Add("Leave/Holiday"); t.Columns.Add("Late count"); t.Columns.Add("Late time");
            t.Columns.Add("Worked"); t.Columns.Add("Overtime");
            t.Columns.Add("Late deduction (days)"); t.Columns.Add("Payable days");
            t.Columns.Add("Salary"); t.Columns.Add("Overtime pay"); t.Columns.Add("Net pay");

            foreach (var g in data.GroupBy(d => d.EnrollNumber).OrderBy(g => SortKey(g.Key)))
            {
                names.TryGetValue(g.Key, out var emp);
                shiftByEnroll.TryGetValue(g.Key, out var shift);
                double salary = emp?.Salary ?? 0;
                double shiftHours = ShiftHours(shift);

                int workingDays = g.Count(x => x.IsWorkingDay);
                int present = g.Count(x => x.Present);
                int absent = g.Count(x => x.Absent);
                int lateCount = g.Count(x => x.Late);
                int leaveDays = g.Count(x => x.Category == DayCategory.Off &&
                    !string.IsNullOrEmpty(x.OffReason) && x.OffReason != "Weekend");
                double otHours = g.Sum(x => x.OvertimeHours);

                // Configurable rule: every N late arrivals = one day's deduction.
                double lateDeductionDays = latesPerDeduction > 0 ? (double)lateCount / latesPerDeduction : 0;
                double payableDays = present - lateDeductionDays;
                if (payableDays < 0) payableDays = 0;

                // Money: daily rate from salary / working days; OT paid at the hourly rate.
                double dailyRate = workingDays > 0 ? salary / workingDays : 0;
                double basePay = payableDays * dailyRate;
                double hourlyRate = shiftHours > 0 ? dailyRate / shiftHours : 0;
                double otPay = otHours * hourlyRate;
                double netPay = basePay + (includeOvertime ? otPay : 0);

                t.Rows.Add(
                    g.Key, Nm(names, g.Key), Cnic(names, g.Key), Dept(names, g.Key), Desig(names, g.Key),
                    ShiftDisplay(shift),
                    workingDays, present, absent, leaveDays, lateCount,
                    DurationFormat.Minutes(g.Where(x => x.Late).Sum(x => x.LateMinutes)),
                    DurationFormat.Hours(g.Sum(x => x.WorkedHours)),
                    DurationFormat.Hours(otHours),
                    lateDeductionDays.ToString("0.##"),
                    payableDays.ToString("0.##"),
                    salary.ToString("0"),
                    otPay.ToString("0"),
                    netPay.ToString("0"));
            }
            return t;
        }

        private static DataTable MonthlySummary(List<DailyAttendance> data, IDictionary<string, Data.Entities.Employee> names,
            IDictionary<string, Data.Entities.Shift> shiftByEnroll)
        {
            // Build per-employee rows with a numeric % so we can rank them.
            var summaries = data.GroupBy(d => d.EnrollNumber).Select(g =>
            {
                shiftByEnroll.TryGetValue(g.Key, out var shift);
                double shiftHours = ShiftHours(shift);
                int workingDays = g.Count(x => x.IsWorkingDay);
                double workedWorkingDays = g.Where(x => x.IsWorkingDay).Sum(x => x.WorkedHours);
                double expected = workingDays * shiftHours;
                return new
                {
                    Enroll = g.Key,
                    Shift = ShiftDisplay(shift),
                    WorkingDays = workingDays,
                    Present = g.Count(x => x.Present),
                    Absent = g.Count(x => x.Absent),
                    LateDays = g.Count(x => x.Late),
                    LateMinutes = g.Where(x => x.Late).Sum(x => x.LateMinutes),
                    Worked = g.Sum(x => x.WorkedHours),
                    Pct = (shiftHours > 0 && expected > 0) ? (double?)(workedWorkingDays / expected * 100) : null
                };
            })
            .OrderByDescending(x => x.Pct ?? -1)   // highest attendance first; no-shift to the bottom
            .ThenBy(x => SortKey(x.Enroll))
            .ToList();

            var t = new DataTable();
            t.Columns.Add("Rank"); t.Columns.Add("Enroll"); t.Columns.Add("Name"); t.Columns.Add("Department"); t.Columns.Add("Shift");
            t.Columns.Add("Working days"); t.Columns.Add("Present"); t.Columns.Add("Absent");
            t.Columns.Add("Late days"); t.Columns.Add("Late time"); t.Columns.Add("Worked"); t.Columns.Add("Attendance %");

            int rank = 1;
            foreach (var x in summaries)
            {
                t.Rows.Add(
                    rank++, x.Enroll, Nm(names, x.Enroll), Dept(names, x.Enroll), x.Shift,
                    x.WorkingDays, x.Present, x.Absent, x.LateDays,
                    DurationFormat.Minutes(x.LateMinutes),
                    DurationFormat.Hours(x.Worked),
                    x.Pct.HasValue ? x.Pct.Value.ToString("0.#") + "%" : "—");
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
                System.Diagnostics.Process.Start(dlg.FileName);   // open in the default app
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), "Export failed: " + ex.Message, "Export", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExportPdfButton_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null || _current.Rows.Count == 0) return;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PDF (*.pdf)|*.pdf",
                FileName = $"Dawloom_{_reportName.Replace(' ', '_')}_{DateTime.Now:yyyyMMdd}.pdf"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var headers = _current.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToList();
                var rows = _current.Rows.Cast<DataRow>()
                    .Select(r => (IList<string>)r.ItemArray.Select(v => v?.ToString() ?? "").ToList())
                    .ToList();
                PdfExport.Write(dlg.FileName, "Attendance " + _reportName, _periodText, headers, rows);
                System.Diagnostics.Process.Start(dlg.FileName);   // open in the default app
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), "PDF export failed: " + ex.Message, "Export", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
