using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using DawloomAttendance.Data;
using DawloomAttendance.Data.Entities;

namespace DawloomAttendance.Views
{
    /// <summary>CRUD for the holiday calendar (used by the Phase 3 calc engine to skip non-working days).</summary>
    public partial class HolidaysWindow : Window
    {
        private readonly AppDb _db;
        private ObservableCollection<Holiday> _holidays;

        public HolidaysWindow(AppDb db)
        {
            InitializeComponent();
            _db = db;
            Reload();
        }

        private void Reload()
        {
            _holidays = new ObservableCollection<Holiday>(_db.GetHolidays());
            Grid.ItemsSource = _holidays;
            LoadWeeklyOff();
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
            var off = _db.GetWeeklyOffDays();
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

                var offDays = WeekBoxes.Where(w => w.box.IsChecked == true).Select(w => w.day);
                _db.SetWeeklyOffDays(offDays);

                MessageBox.Show(this, "Holidays saved.", "Save", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Save failed: " + ex.Message, "Save", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
