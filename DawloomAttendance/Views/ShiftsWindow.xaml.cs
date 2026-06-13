using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using DawloomAttendance.Data;
using DawloomAttendance.Data.Entities;

namespace DawloomAttendance.Views
{
    /// <summary>CRUD for shift templates used by employees (and the Phase 3 calc engine).</summary>
    public partial class ShiftsWindow : Window
    {
        private readonly AppDb _db;
        private ObservableCollection<Shift> _shifts;

        public ShiftsWindow(AppDb db)
        {
            InitializeComponent();
            _db = db;
            Reload();
        }

        private void Reload()
        {
            _shifts = new ObservableCollection<Shift>(_db.GetShifts());
            Grid.ItemsSource = _shifts;
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e) => Reload();

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            var shift = new Shift { Name = "New Shift" };
            _shifts.Add(shift);
            Grid.SelectedItem = shift;
            Grid.ScrollIntoView(shift);
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = Grid.SelectedItems.Cast<Shift>().ToList();
            if (selected.Count == 0) return;
            if (MessageBox.Show(this, $"Delete {selected.Count} shift(s)? Employees on them will be unassigned.",
                    "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            foreach (var s in selected)
            {
                if (s.Id > 0) _db.DeleteShift(s.Id);
                _shifts.Remove(s);
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            Grid.CommitEdit();
            try
            {
                foreach (var s in _shifts)
                {
                    if (string.IsNullOrWhiteSpace(s.Name))
                        throw new InvalidOperationException("Shift name is required.");
                    if (!IsValidTime(s.StartTime) || !IsValidTime(s.EndTime))
                        throw new InvalidOperationException($"Shift '{s.Name}': times must be HH:mm (e.g. 09:00).");
                    if (!IsValidWeekend(s.WeekendDays))
                        throw new InvalidOperationException($"Shift '{s.Name}': weekend days must be numbers 0-6 (e.g. 0 or 5,0).");

                    if (s.Id == 0) s.Id = _db.InsertShift(s);
                    else _db.UpdateShift(s);
                }
                MessageBox.Show(this, "Shifts saved.", "Save", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Save failed: " + ex.Message, "Save", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static bool IsValidTime(string s)
            => TimeSpan.TryParseExact(s, @"hh\:mm", CultureInfo.InvariantCulture, out _);

        private static bool IsValidWeekend(string csv)
        {
            if (string.IsNullOrWhiteSpace(csv)) return true; // no weekend is allowed
            foreach (var tok in csv.Split(','))
            {
                if (!int.TryParse(tok.Trim(), out var d) || d < 0 || d > 6) return false;
            }
            return true;
        }
    }
}
