using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using DawloomAttendance.Data;
using DawloomAttendance.Data.Entities;

namespace DawloomAttendance.Views
{
    /// <summary>CRUD for the holiday calendar (used by the Phase 3 calc engine to skip non-working days).</summary>
    public partial class HolidaysWindow : System.Windows.Controls.UserControl
    {
        private readonly AppDb _db;
        private ObservableCollection<Holiday> _holidays;
        private bool _loadingScope;
        private int _currentScope;   // 0 = whole year, 1-12 = month

        public HolidaysWindow(AppDb db)
        {
            InitializeComponent();
            _db = db;
            InitScopeCombo();
            LoadEmployeeChoices();
            Reload();
        }

        private void LoadEmployeeChoices()
        {
            var choices = new List<EmpChoice> { new EmpChoice { Enroll = null, Display = "(All employees)" } };
            choices.AddRange(_db.GetEmployees().Select(e => new EmpChoice
            {
                Enroll = e.EnrollNumber,
                Display = $"{e.EnrollNumber} - {e.Name}"
            }));
            EmployeeColumn.ItemsSource = choices;
        }

        private class EmpChoice
        {
            public string Enroll { get; set; }
            public string Display { get; set; }
        }

        private void InitScopeCombo()
        {
            var options = new List<ScopeOption> { new ScopeOption { Month = 0, Label = "Whole year" } };
            for (int m = 1; m <= 12; m++)
                options.Add(new ScopeOption { Month = m, Label = CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(m) });
            ScopeCombo.ItemsSource = options;
            _currentScope = 0;
            _loadingScope = true;
            ScopeCombo.SelectedValue = 0;
            _loadingScope = false;
        }

        private void Reload()
        {
            _holidays = new ObservableCollection<Holiday>(_db.GetHolidays());
            Grid.ItemsSource = _holidays;
            LoadWeeklyOff();
        }

        private void ScopeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_loadingScope) return;
            SaveCurrentScope();                          // persist the scope we're leaving
            _currentScope = (int)ScopeCombo.SelectedValue;
            LoadWeeklyOff();                             // load the scope we're entering
        }

        private void SaveCurrentScope()
        {
            var offDays = WeekBoxes.Where(w => w.box.IsChecked == true).Select(w => w.day);
            _db.SetWeeklyOffDays(_currentScope, offDays);
        }

        private class ScopeOption
        {
            public int Month { get; set; }
            public string Label { get; set; }
        }

        // (CheckBox, day-of-week number) pairs; 0=Sun … 6=Sat.
        private (System.Windows.Controls.CheckBox box, int day)[] WeekBoxes =>
            new[]
            {
                (SunBox, 0), (MonBox, 1), (TueBox, 2), (WedBox, 3),
                (ThuBox, 4), (FriBox, 5), (SatBox, 6)
            };

        private void LoadWeeklyOff()
        {
            var off = _db.GetWeeklyOffDays(_currentScope);
            foreach (var (box, day) in WeekBoxes)
                box.IsChecked = off.Contains(day);
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e) => Reload();

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            var h = new Holiday { Date = DateTime.Today, Label = "Holiday" };
            _holidays.Add(h);
            Grid.SelectedItem = h;
            Grid.ScrollIntoView(h);
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = Grid.SelectedItems.Cast<Holiday>().ToList();
            if (selected.Count == 0) return;

            foreach (var h in selected)
            {
                if (h.Id > 0) _db.DeleteHoliday(h.Id);
                _holidays.Remove(h);
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            Grid.CommitEdit();
            try
            {
                foreach (var h in _holidays)
                {
                    if (string.IsNullOrWhiteSpace(h.Label))
                        throw new InvalidOperationException("Each holiday needs a label.");

                    if (h.Id == 0) h.Id = _db.InsertHoliday(h);
                    else _db.UpdateHoliday(h);
                }

                SaveCurrentScope();

                MessageBox.Show(Window.GetWindow(this), "Holidays saved.", "Save", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), "Save failed: " + ex.Message, "Save", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
