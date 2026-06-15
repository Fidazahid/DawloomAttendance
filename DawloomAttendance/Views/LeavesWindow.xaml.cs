using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DawloomAttendance.Data;
using DawloomAttendance.Data.Entities;

namespace DawloomAttendance.Views
{
    /// <summary>
    /// Per-employee leave management: yearly balances (entitled / taken / remaining) by
    /// type, and the recorded leave days. Adding a date range records one entry per day,
    /// skipping weekends/holidays so balance isn't spent on days that are already off.
    /// </summary>
    public partial class LeavesWindow : UserControl
    {
        private readonly AppDb _db;
        private List<LeaveType> _types;
        private bool _loading;

        public LeavesWindow(AppDb db)
        {
            InitializeComponent();
            _db = db;
            Loaded += (_, __) => InitOnce();
        }

        private void InitOnce()
        {
            _loading = true;

            _types = _db.GetLeaveTypes(activeOnly: true).ToList();
            TypeBox.ItemsSource = _types;
            if (_types.Count > 0) TypeBox.SelectedIndex = 0;

            EmployeeBox.ItemsSource = _db.GetEmployees()
                .Where(e => e.Active)
                .Select(e => new EmpChoice { Enroll = e.EnrollNumber, Display = $"{e.EnrollNumber} - {e.Name}" })
                .ToList();

            int thisYear = DateTime.Today.Year;
            YearBox.ItemsSource = Enumerable.Range(thisYear - 3, 5).Reverse().ToList();
            YearBox.SelectedItem = thisYear;

            FromDate.SelectedDate = DateTime.Today;
            ToDate.SelectedDate = DateTime.Today;

            if (EmployeeBox.Items.Count > 0) EmployeeBox.SelectedIndex = 0;

            _loading = false;
            Reload();
        }

        private string SelectedEnroll => (EmployeeBox.SelectedValue as string);
        private int SelectedYear => YearBox.SelectedItem is int y ? y : DateTime.Today.Year;

        private void Selection_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_loading) Reload();
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e) => Reload();

        private void Reload()
        {
            var enroll = SelectedEnroll;
            if (string.IsNullOrEmpty(enroll)) { BalanceGrid.ItemsSource = null; EntryGrid.ItemsSource = null; return; }

            BalanceGrid.ItemsSource = _db.GetLeaveBalances(enroll, SelectedYear);

            var typeName = _types.ToDictionary(t => t.Id, t => t.Name);
            EntryGrid.ItemsSource = new ObservableCollection<LeaveEntryRow>(
                _db.GetLeaveEntries(enroll, SelectedYear).Select(le => new LeaveEntryRow
                {
                    Id = le.Id,
                    Date = le.Date,
                    TypeName = typeName.TryGetValue(le.LeaveTypeId, out var n) ? n : "?",
                    Reason = le.Reason
                }));
        }

        private void BalanceGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit) return;
            if (!(e.Row.Item is LeaveBalance bal)) return;

            // The bound Entitled is updated by the grid; persist it as this employee's override.
            // Defer the read so the binding has written the new value back to the object.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _db.SetEntitlement(SelectedEnroll, bal.LeaveTypeId, SelectedYear, bal.Entitled);
                Reload();   // recompute Remaining
            }));
        }

        private void AddLeave_Click(object sender, RoutedEventArgs e)
        {
            var enroll = SelectedEnroll;
            if (string.IsNullOrEmpty(enroll)) { Warn("Select an employee first."); return; }
            if (!(TypeBox.SelectedItem is LeaveType type)) { Warn("Pick a leave type."); return; }
            if (FromDate.SelectedDate == null || ToDate.SelectedDate == null) { Warn("Pick a From and To date."); return; }

            DateTime from = FromDate.SelectedDate.Value.Date;
            DateTime to = ToDate.SelectedDate.Value.Date;
            if (to < from) { Warn("'To' date is before 'From' date."); return; }

            // Record one entry per day, skipping days that are already off (weekend/holiday)
            // so a paid balance isn't spent on a non-working day.
            int added = 0, skippedOff = 0, skippedDup = 0;
            for (var d = from; d <= to; d = d.AddDays(1))
            {
                if (_db.IsHoliday(d)) { skippedOff++; continue; }
                bool ok = _db.InsertLeaveEntry(new LeaveEntry
                {
                    EnrollNumber = enroll,
                    LeaveTypeId = type.Id,
                    Date = d,
                    Reason = string.IsNullOrWhiteSpace(ReasonBox.Text) ? null : ReasonBox.Text.Trim()
                });
                if (ok) added++; else skippedDup++;
            }

            Reload();

            // Warn if a paid type is now over its allowance (still allowed, just flagged).
            if (type.Paid)
            {
                var bal = _db.GetLeaveBalances(enroll, SelectedYear).FirstOrDefault(b => b.LeaveTypeId == type.Id);
                if (bal != null && bal.Remaining < 0)
                    Warn($"Heads up: {type.Name} is now over the yearly allowance by {-bal.Remaining:0.##} day(s).");
            }

            string msg = $"Added {added} day(s).";
            if (skippedDup > 0) msg += $" {skippedDup} already had leave.";
            if (skippedOff > 0) msg += $" {skippedOff} skipped (weekend/holiday).";
            ReasonBox.Clear();
            MessageBox.Show(Window.GetWindow(this), msg, "Add leave", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void DeleteLeave_Click(object sender, RoutedEventArgs e)
        {
            var selected = EntryGrid.SelectedItems.Cast<LeaveEntryRow>().ToList();
            if (selected.Count == 0) { Warn("Select the leave day(s) to delete."); return; }

            foreach (var row in selected)
                _db.DeleteLeaveEntry(row.Id);
            Reload();
        }

        private void Warn(string msg) =>
            MessageBox.Show(Window.GetWindow(this), msg, "Leaves", MessageBoxButton.OK, MessageBoxImage.Warning);

        private class EmpChoice
        {
            public string Enroll { get; set; }
            public string Display { get; set; }
        }

        private class LeaveEntryRow
        {
            public long Id { get; set; }
            public DateTime Date { get; set; }
            public string TypeName { get; set; }
            public string Reason { get; set; }
        }
    }
}
