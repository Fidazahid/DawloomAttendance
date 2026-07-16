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

        // Reads the period + employee filter and computes attendance for the range.
        // Returns false (after warning) if the date range is invalid.
        private bool GatherData(out DateTime from, out DateTime to,
            out Dictionary<string, Data.Entities.Employee> names,
            out Dictionary<string, Data.Entities.Shift> shiftByEnroll,
            out List<DailyAttendance> data)
        {
            from = FromPick.SelectedDate ?? DateTime.Today;
            to = ToPick.SelectedDate ?? DateTime.Today;
            names = _db.GetEmployees().ToDictionary(x => x.EnrollNumber);
            shiftByEnroll = new Dictionary<string, Data.Entities.Shift>();
            data = null;
            if (to < from) { MessageBox.Show(Window.GetWindow(this), "‘To’ must be on or after ‘From’.", "Reports", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }

            var shifts = _db.GetShifts().ToDictionary(s => s.Id);
            foreach (var emp in names.Values)
                shiftByEnroll[emp.EnrollNumber] =
                    (emp.ShiftId.HasValue && shifts.TryGetValue(emp.ShiftId.Value, out var sh)) ? sh : null;

            var all = new AttendanceService(_db).ComputeForRange(from, to);

            // Optional multi-employee filter: none checked = all; some checked = only those.
            var selected = (EmployeeList.ItemsSource as IEnumerable<EmpPick>)
                .Where(p => p.IsChecked).Select(p => p.Enroll).ToList();
            if (selected.Count > 0)
            {
                var set = new HashSet<string>(selected);
                all = all.Where(d => set.Contains(d.EnrollNumber)).ToList();
            }
            data = all;
            return true;
        }

        private void RunButton_Click(object sender, RoutedEventArgs e)
        {
            if (!GatherData(out var from, out var to, out var names, out var shiftByEnroll, out var data)) return;

            switch (SelectedType)
            {
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

        private static DataTable MonthlySummary(List<DailyAttendance> data, IDictionary<string, Data.Entities.Employee> names,
            IDictionary<string, Data.Entities.Shift> shiftByEnroll)
        {
            // Build per-employee rows with a numeric % so we can rank them.
            // Attendance % is days-based (present ÷ working days) — the same formula the
            // emailed per-employee report uses (see EmployeeReport.BuildSummary), so the two
            // reports always agree.
            var summaries = data.GroupBy(d => d.EnrollNumber).Select(g =>
            {
                shiftByEnroll.TryGetValue(g.Key, out var shift);
                int workingDays = g.Count(x => x.IsWorkingDay);
                int present = g.Count(x => x.Present);
                return new
                {
                    Enroll = g.Key,
                    Shift = ShiftDisplay(shift),
                    WorkingDays = workingDays,
                    Present = present,
                    Absent = g.Count(x => x.Absent),
                    LateDays = g.Count(x => x.Late),
                    LateMinutes = g.Where(x => x.Late).Sum(x => x.LateMinutes),
                    Worked = g.Sum(x => x.WorkedHours),
                    Pct = workingDays > 0 ? (double?)(100.0 * present / workingDays) : null
                };
            })
            .OrderByDescending(x => x.Pct ?? -1)   // highest attendance first
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

        // ---- Email sending -----------------------------------------------------------

        private List<Data.Entities.Employee> SelectedEmployees()
        {
            var all = _db.GetEmployees().ToList();
            var picked = new HashSet<string>((EmployeeList.ItemsSource as IEnumerable<EmpPick>)
                .Where(p => p.IsChecked).Select(p => p.Enroll));
            return picked.Count == 0 ? all : all.Where(e => picked.Contains(e.EnrollNumber)).ToList();
        }

        private async void EmailReportsButton_Click(object sender, RoutedEventArgs e)
        {
            var from = FromPick.SelectedDate ?? DateTime.Today;
            var to = ToPick.SelectedDate ?? DateTime.Today;
            if (to < from) { MessageBox.Show(Window.GetWindow(this), "‘To’ must be on or after ‘From’.", "Email", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

            var email = EmailSettings.Load();
            if (!email.IsConfigured)
            {
                MessageBox.Show(Window.GetWindow(this), "Email isn’t configured yet. Open Settings and fill in the SMTP server + From address.",
                    "Email", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var recipients = SelectedEmployees();
            if (recipients.Count == 0) { MessageBox.Show(Window.GetWindow(this), "No employees to send to.", "Email", MessageBoxButton.OK, MessageBoxImage.Information); return; }

            if (MessageBox.Show(Window.GetWindow(this),
                    $"Email the attendance report ({from:yyyy-MM-dd} to {to:yyyy-MM-dd}) to {recipients.Count} employee(s)?",
                    "Send via Email", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            EmailReportsButton.IsEnabled = false;
            Summary.Text = "Sending emails …";
            try
            {
                var progress = new Progress<string>(m => Summary.Text = m);   // live "sending to whom"
                var outcomes = await System.Threading.Tasks.Task.Run(() =>
                    ReportMailer.Send(_db, email, recipients, from, to, email.SubjectManual, "manual",
                        periodKey: null, skipAlreadySent: false, progress: progress));
                ShowOutcomeSummary("Send via Email", outcomes);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), "Email failed: " + ex.Message, "Email", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                EmailReportsButton.IsEnabled = true;
            }
        }

        private void ShowOutcomeSummary(string title, List<EmailSendOutcome> outcomes)
        {
            int sent = outcomes.Count(o => o.Sent);
            var missing = outcomes.Where(o => !o.Sent && o.Error == "no email address").ToList();
            var errors = outcomes.Where(o => !o.Sent && o.Error != "no email address").ToList();

            var msg = $"Sent {sent} of {outcomes.Count}.";
            if (missing.Count > 0)
                msg += "\n\nMissing email (not sent):\n" + string.Join("\n", missing.Select(m => $"  {m.Enroll} - {m.Name}"));
            if (errors.Count > 0)
                msg += "\n\nFailed:\n" + string.Join("\n", errors.Take(10).Select(m => $"  {m.Enroll} - {m.Name}: {m.Error}")) +
                       (errors.Count > 10 ? $"\n  … and {errors.Count - 10} more." : "");

            Summary.Text = $"Email: sent {sent}, {missing.Count} missing email, {errors.Count} failed.";
            MessageBox.Show(Window.GetWindow(this), msg, title, MessageBoxButton.OK,
                (missing.Count > 0 || errors.Count > 0) ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
    }
}
