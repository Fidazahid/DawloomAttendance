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
            Reload();
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

            try
            {
                var slips = SalaryService.GenerateMonth(_db, from, to);
                Reload();
                SelectKey(key);
                StatusText.Text = $"Generated {slips.Count} slip(s) for {from:MMMM yyyy}. Loans deducted where outstanding.";
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
                PdfExport.WriteSalarySlips(dlg.FileName, b.MonthYear, slips);
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

            var slips = SalaryService.Load(_db, b.PeriodKey);
            if (slips.Count == 0) { Warn("No slips stored for this month."); return; }

            if (MessageBox.Show(Window.GetWindow(this),
                    $"Email {b.MonthYear} salary slips to employees?\n(Interns with no salary are skipped.)",
                    "Send Slips", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            SendButton.IsEnabled = false;
            StatusText.Text = "Sending salary slips …";
            try
            {
                var names = _db.GetEmployees().ToDictionary(x => x.EnrollNumber);
                var outcomes = await Task.Run(() => SalaryService.SendSlips(_db, email, b.MonthYear, slips, names));
                _db.MarkBatchSent(b.PeriodKey, DateTime.Now);
                Reload();
                SelectKey(b.PeriodKey);

                int sent = outcomes.Count(o => o.Sent);
                int interns = outcomes.Count(o => !o.Sent && o.Error != null && o.Error.StartsWith("no salary"));
                int noEmail = outcomes.Count(o => !o.Sent && o.Error == "no email address");
                int failed = outcomes.Count(o => !o.Sent) - interns - noEmail;
                StatusText.Text = $"Sent {sent}. Interns skipped: {interns}. No email: {noEmail}. Failed: {failed}.";
                MessageBox.Show(Window.GetWindow(this),
                    $"Sent {sent} of {outcomes.Count}.\nInterns skipped (no salary): {interns}\nNo email address: {noEmail}\nFailed: {failed}",
                    "Send Slips", MessageBoxButton.OK,
                    failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), "Send failed: " + ex.Message, "Send Slips",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { SendButton.IsEnabled = true; }
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
        }

        private void Warn(string msg) =>
            MessageBox.Show(Window.GetWindow(this), msg, "Salary", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
