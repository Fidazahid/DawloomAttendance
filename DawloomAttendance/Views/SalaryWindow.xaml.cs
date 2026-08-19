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
            var batches = _db.GetSalaryBatches();
            BatchGrid.ItemsSource = batches;
            StatusText.Text = $"{batches?.Count ?? 0} generated month(s).";
            if (prev != null) SelectKey(prev);

            // Land on a month straight away, otherwise the employee tick-list and the
            // action buttons all sit there empty until you happen to click a row.
            if (Selected == null && batches != null && batches.Count > 0)
                BatchGrid.SelectedItem = batches[0];
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

            var allSlips = SalaryService.Load(_db, b.PeriodKey);
            if (allSlips.Count == 0) { Warn("No slips stored for this month."); return; }

            bool isAll; string who;
            var slips = ApplySelection(allSlips, out isAll, out who);
            if (slips.Count == 0) { Warn("No slip stored for the ticked employee(s) this month."); return; }

            // One person gets their own name on the file; a subset says so, so a PDF
            // holding 3 of 30 slips can't be mistaken for the full month's run.
            string stem = isAll ? "Dawloom_SalarySlips"
                : slips.Count == 1 ? $"Dawloom_SalarySlip_{slips[0].Enroll}"
                : $"Dawloom_SalarySlips_{slips.Count}_selected";

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PDF (*.pdf)|*.pdf",
                FileName = $"{stem}_{b.PeriodFrom:yyyy_MM}.pdf"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                PdfExport.WriteSalarySlips(dlg.FileName, b.MonthYear, slips, AllowAbove100);
                System.Diagnostics.Process.Start(dlg.FileName);
                StatusText.Text = $"Created {slips.Count} slip(s) for {b.MonthYear} — {who}.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), "PDF failed: " + ex.Message, "Salary",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RecalcButton_Click(object sender, RoutedEventArgs e)
        {
            var b = Selected;
            if (b == null) { Warn("Select a month first."); return; }

            if (MessageBox.Show(Window.GetWindow(this),
                    $"Recalculate Expected hours and Attendance % for {b.MonthYear} from the punch data?\n\n" +
                    "Pay, loan deductions and net pay are NOT changed.",
                    "Recalculate attendance", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            try
            {
                int n = SalaryService.RecalculateAttendance(_db, b.PeriodKey);
                StatusText.Text = $"Recalculated attendance on {n} slip(s) for {b.MonthYear}. Pay unchanged.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), "Recalculate failed: " + ex.Message, "Salary",
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

            // Recipients: whoever is ticked in the dropdown (nothing ticked = everyone).
            bool sendAll; string who;
            var slips = ApplySelection(allSlips, out sendAll, out who);
            if (slips.Count == 0) { Warn("No slip stored for the ticked employee(s) this month."); return; }

            string prompt = sendAll
                ? $"Email {b.MonthYear} salary slips to all employees?\n(Interns with no salary are skipped.)"
                : $"Email the {b.MonthYear} salary slip to {who}?\n\n" +
                  string.Join("\n", slips.Take(15).Select(s => $"   • {s.Enroll} — {s.Name}")) +
                  (slips.Count > 15 ? $"\n   … and {slips.Count - 15} more" : "");
            if (MessageBox.Show(Window.GetWindow(this), prompt, "Send Slips",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            SendButton.IsEnabled = false;
            StatusText.Text = sendAll ? "Sending salary slips …" : $"Sending slip to {who} …";
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

        /// <summary>
        /// One tickable row in the employee dropdown: "All employees" (Enroll == null,
        /// a select-all/none toggle) plus each employee with a slip that month.
        /// </summary>
        private sealed class RecipientItem : System.ComponentModel.INotifyPropertyChanged
        {
            public string Enroll { get; set; }   // null = the "All employees" toggle row
            public string Display { get; set; }
            public string Weight => Enroll == null ? "Bold" : "Normal";

            private bool _isSelected;
            public bool IsSelected
            {
                get { return _isSelected; }
                set
                {
                    if (_isSelected == value) return;
                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }

            public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        }

        private List<RecipientItem> _recipients = new List<RecipientItem>();
        private bool _syncingTicks;   // guards the All-row <-> per-person cascade

        private void PopulateRecipients(SalaryBatch b)
        {
            foreach (var old in _recipients) old.PropertyChanged -= Recipient_PropertyChanged;

            _recipients = new List<RecipientItem> { new RecipientItem { Enroll = null, Display = "All employees" } };
            if (b != null)
                foreach (var s in _db.GetSlipSnapshots(b.PeriodKey))
                    _recipients.Add(new RecipientItem { Enroll = s.Enroll, Display = $"{s.Enroll} — {s.Name}" });

            foreach (var r in _recipients) r.PropertyChanged += Recipient_PropertyChanged;
            RecipientCombo.ItemsSource = _recipients;
            UpdateRecipientSummary();
        }

        private IEnumerable<RecipientItem> People => _recipients.Where(r => r.Enroll != null);

        /// <summary>Ticked employees; empty means "no filter" — i.e. everyone.</summary>
        private List<string> SelectedEnrolls => People.Where(r => r.IsSelected).Select(r => r.Enroll).ToList();

        private void Recipient_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (_syncingTicks) return;
            _syncingTicks = true;
            try
            {
                var item = (RecipientItem)sender;
                if (item.Enroll == null)
                {
                    // The All row is a plain select-all / clear-all switch.
                    foreach (var r in People) r.IsSelected = item.IsSelected;
                }
                else
                {
                    // Keep the All row honest: ticked only when every person is ticked.
                    var all = _recipients.First();
                    all.IsSelected = People.Any() && People.All(r => r.IsSelected);
                }
            }
            finally { _syncingTicks = false; }
            UpdateRecipientSummary();
        }

        private void UpdateRecipientSummary()
        {
            var picked = SelectedEnrolls;
            RecipientSummary.Text =
                picked.Count == 0 || picked.Count == People.Count() ? "All employees"
                : picked.Count == 1 ? People.First(r => r.IsSelected).Display
                : $"{picked.Count} selected";
        }

        // A multi-tick list has no meaningful SelectedItem; clicking the row margin would
        // otherwise leave one highlighted underneath the caption.
        private void RecipientCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (RecipientCombo.SelectedIndex >= 0) RecipientCombo.SelectedIndex = -1;
        }

        /// <summary>
        /// Narrows a month's slips to the ticked employees. Nothing ticked (or everyone
        /// ticked) = the whole month, which is what <paramref name="isAll"/> reports —
        /// only a whole-month send may mark the batch as sent.
        /// </summary>
        private IReadOnlyList<SalarySlip> ApplySelection(
            IReadOnlyList<SalarySlip> all, out bool isAll, out string who)
        {
            var picked = SelectedEnrolls;
            if (picked.Count == 0 || picked.Count == People.Count())
            {
                isAll = true;
                who = "all employees";
                return all;
            }

            var set = new HashSet<string>(picked);
            var subset = (IReadOnlyList<SalarySlip>)all.Where(s => set.Contains(s.Enroll)).ToList();
            isAll = false;
            who = subset.Count == 1
                ? (subset[0].Name ?? subset[0].Enroll)
                : $"{subset.Count} selected employees";
            return subset;
        }

        private void Warn(string msg) =>
            MessageBox.Show(Window.GetWindow(this), msg, "Salary", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
