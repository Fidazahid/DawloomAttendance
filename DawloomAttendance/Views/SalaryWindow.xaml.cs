using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using DawloomAttendance.Data;
using DawloomAttendance.Data.Entities;
using DawloomAttendance.Services;

namespace DawloomAttendance.Views
{
    /// <summary>
    /// Salary tab: generate a month's slips (snapshotted with loan deductions), re-generate
    /// the exact PDF for any past month, email slips manually (skipping interns), and set a
    /// per-month auto-send date. Payroll lives here now, not in Reports.
    /// </summary>
    public partial class SalaryWindow : UserControl
    {
        private readonly AppDb _db;

        public SalaryWindow(AppDb db)
        {
            InitializeComponent();
            _db = db;
            InitPickers();
            LoadGenerateOptions();
            Reload();
        }

        private bool _loadingOptions;

        // The two generate options persist between sessions (default: both on).
        private void LoadGenerateOptions()
        {
            _loadingOptions = true;   // don't let these programmatic sets re-save (and clobber) each other
            OvertimeCheck.IsChecked = _db.GetSetting("Salary.IncludeOvertime") != "0";
            DeductionCheck.IsChecked = _db.GetSetting("Salary.ApplyDeduction") != "0";
            AllowAbove100Check.IsChecked = AttendancePercentage.AllowAbove100(_db);
            _loadingOptions = false;
        }

        /// <summary>Display-only cap for the attendance % on the slip; shared app-wide with Reports.</summary>
        private bool AllowAbove100 => AllowAbove100Check.IsChecked == true;

        private void AllowAbove100_Changed(object sender, RoutedEventArgs e)
        {
            if (_loadingOptions || _db == null) return;
            AttendancePercentage.SetAllowAbove100(_db, AllowAbove100);
        }

        // Persist each toggle immediately so the checkboxes keep their state across tabs/sessions.
        private void Options_Changed(object sender, RoutedEventArgs e)
        {
            if (_db == null || _loadingOptions) return;   // skip during InitializeComponent / load
            _db.SetSetting("Salary.IncludeOvertime", OvertimeCheck.IsChecked == true ? "1" : "0");
            _db.SetSetting("Salary.ApplyDeduction", DeductionCheck.IsChecked == true ? "1" : "0");
        }

        private void InitPickers()
        {
            MonthCombo.ItemsSource = Enumerable.Range(1, 12)
                .Select(m => CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(m)).ToList();
            int thisYear = DateTime.Today.Year;
            YearCombo.ItemsSource = Enumerable.Range(thisYear - 3, 5).Reverse().ToList();

            var prev = DateTime.Today.AddMonths(-1);   // default to last month
            MonthCombo.SelectedIndex = prev.Month - 1;
            YearCombo.SelectedItem = prev.Year;
        }

        private SalaryBatch Selected => BatchGrid.SelectedItem as SalaryBatch;

        private void Reload()
        {
            var prev = Selected?.PeriodKey;
            BatchGrid.ItemsSource = _db.GetSalaryBatches();
            StatusText.Text = $"{(BatchGrid.ItemsSource as IEnumerable<SalaryBatch>)?.Count() ?? 0} generated month(s).";
            if (prev != null) SelectKey(prev);
        }

        private void SelectKey(string key)
        {
            var m = (BatchGrid.ItemsSource as IEnumerable<SalaryBatch>)?.FirstOrDefault(x => x.PeriodKey == key);
            if (m != null) BatchGrid.SelectedItem = m;
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e) => Reload();

        private void GenerateButton_Click(object sender, RoutedEventArgs e)
        {
            if (MonthCombo.SelectedIndex < 0 || !(YearCombo.SelectedItem is int year)) return;
            int month = MonthCombo.SelectedIndex + 1;
            var from = new DateTime(year, month, 1);
            var to = from.AddMonths(1).AddDays(-1);
            var key = ReportPeriods.MonthKey(from);

            if (_db.SalaryBatchExists(key))
            {
                MessageBox.Show(Window.GetWindow(this),
                    $"{from:MMMM yyyy} is already generated. Use “Re-generate PDF” to reproduce it, or “Send now” to email it.",
                    "Salary", MessageBoxButton.OK, MessageBoxImage.Information);
                SelectKey(key);
                return;
            }

            bool overtime = OvertimeCheck.IsChecked == true;
            bool deduction = DeductionCheck.IsChecked == true;

            try
            {
                var slips = SalaryService.GenerateMonth(_db, from, to, overtime, deduction);
                Reload();
                SelectKey(key);
                StatusText.Text = $"Generated {slips.Count} slip(s) for {from:MMMM yyyy}. " +
                    $"Overtime {(overtime ? "included" : "excluded")}, " +
                    $"deduction {(deduction ? "applied" : "off")}. Loans deducted where outstanding.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), "Generate failed: " + ex.Message, "Salary",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RegenButton_Click(object sender, RoutedEventArgs e)
        {
            var b = Selected;
            if (b == null) { Warn("Select a month first."); return; }

            var slips = SalaryService.Load(_db, b.PeriodKey);
            if (slips.Count == 0) { Warn("No slips stored for this month."); return; }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PDF (*.pdf)|*.pdf",
                FileName = $"Dawloom_SalarySlips_{b.PeriodFrom:yyyy_MM}.pdf"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                PdfExport.WriteSalarySlips(dlg.FileName, b.MonthYear, slips, AllowAbove100);
                System.Diagnostics.Process.Start(dlg.FileName);
                StatusText.Text = $"Re-generated {slips.Count} slip(s) for {b.MonthYear}.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), "PDF failed: " + ex.Message, "Salary",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            var b = Selected;
            if (b == null) { Warn("Select a month first."); return; }

            var email = EmailSettings.Load();
            if (!email.IsConfigured)
            {
                MessageBox.Show(Window.GetWindow(this),
                    "Email isn’t configured. Open Settings and fill in the SMTP server + From address.",
                    "Send Slips", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var allSlips = SalaryService.Load(_db, b.PeriodKey);
            if (allSlips.Count == 0) { Warn("No slips stored for this month."); return; }

            // Recipient: "All employees" (Enroll == null) or one selected person.
            var recipient = RecipientCombo.SelectedItem as RecipientItem;
            bool sendAll = recipient == null || recipient.Enroll == null;
            var slips = sendAll
                ? allSlips
                : (IReadOnlyList<SalarySlip>)allSlips.Where(s => s.Enroll == recipient.Enroll).ToList();
            if (slips.Count == 0) { Warn("No slip stored for that employee this month."); return; }

            string prompt = sendAll
                ? $"Email {b.MonthYear} salary slips to all employees?\n(Interns with no salary are skipped.)"
                : $"Email the {b.MonthYear} salary slip to {recipient.Display}?";
            if (MessageBox.Show(Window.GetWindow(this), prompt, "Send Slips",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            SendButton.IsEnabled = false;
            StatusText.Text = sendAll ? "Sending salary slips …" : $"Sending slip to {recipient.Display} …";
            try
            {
                var names = _db.GetEmployees().ToDictionary(x => x.EnrollNumber);
                // Live progress: shows who each slip is going to (marshalled to the UI thread).
                var progress = new Progress<string>(m => StatusText.Text = m);
                var outcomes = await Task.Run(() => SalaryService.SendSlips(_db, email, b.MonthYear, slips, names, progress));
                // Only a full send marks the whole month as sent (and freezes it); a targeted
                // one-off send just re-mails that person's copy without changing the month's state.
                if (sendAll) _db.MarkBatchSent(b.PeriodKey, DateTime.Now);
                Reload();
                SelectKey(b.PeriodKey);
                ShowSendSummary(b.MonthYear, outcomes);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), "Send failed: " + ex.Message, "Send Slips",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { SendButton.IsEnabled = true; }
        }

        /// <summary>Post-send dialog: sent count plus who was skipped/failed, named.</summary>
        private void ShowSendSummary(string monthYear, System.Collections.Generic.List<EmailSendOutcome> outcomes)
        {
            var sent = outcomes.Where(o => o.Sent).ToList();
            var interns = outcomes.Where(o => !o.Sent && o.Error != null && o.Error.StartsWith("no salary")).ToList();
            var noEmail = outcomes.Where(o => !o.Sent && o.Error == "no email address").ToList();
            var failed = outcomes.Where(o => !o.Sent && !interns.Contains(o) && !noEmail.Contains(o)).ToList();

            string List(System.Collections.Generic.List<EmailSendOutcome> xs) =>
                string.Join("\n", xs.Take(20).Select(x => $"   • {x.Enroll} — {x.Name}")) +
                (xs.Count > 20 ? $"\n   … and {xs.Count - 20} more" : "");

            var msg = $"Sent {sent.Count} of {outcomes.Count} for {monthYear}.";
            if (noEmail.Count > 0) msg += $"\n\nNo email address ({noEmail.Count}):\n" + List(noEmail);
            if (interns.Count > 0) msg += $"\n\nInterns skipped — no salary ({interns.Count}):\n" + List(interns);
            if (failed.Count > 0)
                msg += $"\n\nFailed ({failed.Count}):\n" +
                       string.Join("\n", failed.Take(20).Select(x => $"   • {x.Enroll} — {x.Name}: {x.Error}"));

            StatusText.Text = $"Sent {sent.Count}. No email: {noEmail.Count}. Interns: {interns.Count}. Failed: {failed.Count}.";
            bool anyMissing = noEmail.Count > 0 || interns.Count > 0 || failed.Count > 0;
            MessageBox.Show(Window.GetWindow(this), msg, "Send Slips", MessageBoxButton.OK,
                anyMissing ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }

        private void SaveScheduleButton_Click(object sender, RoutedEventArgs e)
        {
            var b = Selected;
            if (b == null) { Warn("Select a month first."); return; }

            bool enabled = SchedEnabled.IsChecked == true;
            if (enabled && SchedDate.SelectedDate == null)
            {
                Warn("Pick a date to auto-send on, or uncheck Enabled.");
                return;
            }
            _db.SetBatchSchedule(b.PeriodKey, SchedDate.SelectedDate, enabled);
            Reload();
            SelectKey(b.PeriodKey);
            StatusText.Text = enabled
                ? $"{b.MonthYear} will auto-send on {SchedDate.SelectedDate:yyyy-MM-dd} (missed → next day)."
                : $"{b.MonthYear} auto-send disabled.";
        }

        private void BatchGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var b = Selected;
            SchedDate.SelectedDate = b?.ScheduleDate;
            SchedEnabled.IsChecked = b?.ScheduleEnabled ?? false;
            PopulateRecipients(b);
        }

        /// <summary>The email target: "All employees" plus each employee with a slip that month.</summary>
        private sealed class RecipientItem
        {
            public string Enroll { get; set; }   // null = all employees
            public string Display { get; set; }
        }

        private void PopulateRecipients(SalaryBatch b)
        {
            var items = new List<RecipientItem> { new RecipientItem { Enroll = null, Display = "All employees" } };
            if (b != null)
                foreach (var s in _db.GetSlipSnapshots(b.PeriodKey))
                    items.Add(new RecipientItem { Enroll = s.Enroll, Display = $"{s.Enroll} — {s.Name}" });
            RecipientCombo.ItemsSource = items;
            RecipientCombo.SelectedIndex = 0;
        }

        private void Warn(string msg) =>
            MessageBox.Show(Window.GetWindow(this), msg, "Salary", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
