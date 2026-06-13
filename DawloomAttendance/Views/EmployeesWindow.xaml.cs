using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using DawloomAttendance.Data;
using DawloomAttendance.Data.Entities;
using DawloomAttendance.Device;

namespace DawloomAttendance.Views
{
    /// <summary>
    /// Employee management: list / add / edit / delete, plus "Import from Device"
    /// which pulls enrolled K70 users and creates an employee row for each new
    /// enrollment number (so raw punches resolve to people).
    /// </summary>
    public partial class EmployeesWindow : System.Windows.Controls.UserControl
    {
        private readonly AppDb _db;
        private readonly IZkDevice _device;
        private readonly int _machineNumber;
        private ObservableCollection<Employee> _employees;

        // ShiftId 0 is an in-memory sentinel for "no shift" so the (None) combo item
        // can be shown/selected; it is converted back to NULL when saving.
        private const long NoShiftId = 0;

        public EmployeesWindow(AppDb db, IZkDevice device, int machineNumber)
        {
            InitializeComponent();
            _db = db;
            _device = device;
            _machineNumber = machineNumber;
            Reload();
        }

        private void Reload()
        {
            LoadShiftChoices();

            _employees = new ObservableCollection<Employee>(_db.GetEmployees());
            foreach (var emp in _employees)
                if (emp.ShiftId == null) emp.ShiftId = NoShiftId;   // show "(None)" rather than blank

            Grid.ItemsSource = _employees;
            StatusText.Text = $"{_employees.Count} employees.";
        }

        private void LoadShiftChoices()
        {
            var choices = new List<Shift> { new Shift { Id = NoShiftId, Name = "(None)" } };
            choices.AddRange(_db.GetShifts());
            ShiftColumn.ItemsSource = choices;
        }

        private void ShiftsButton_Click(object sender, RoutedEventArgs e)
        {
            new ShiftsWindow(_db) { Owner = Window.GetWindow(this) }.ShowDialog();
            Reload();   // pick up shift edits in the dropdown
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e) => Reload();

        private async void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            ImportButton.IsEnabled = false;
            StatusText.Text = "Importing users from device …";
            try
            {
                var users = (await Task.Run(() => _device.ReadAllUsers(_machineNumber))).ToList();
                if (users.Count == 0)
                {
                    MessageBox.Show(Window.GetWindow(this),
                        "No users returned. Make sure the device is connected (green dot on the main window) " +
                        "and the app was built with SDK support.",
                        "Import", MessageBoxButton.OK, MessageBoxImage.Information);
                    StatusText.Text = $"{_employees.Count} employees.";
                    return;
                }

                int added = 0;
                foreach (var u in users)
                    if (_db.InsertEmployeeIfNew(u.EnrollNumber, u.Name))
                        added++;

                Reload();
                StatusText.Text = $"Imported {users.Count} device users — {added} new, {users.Count - added} already known.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), "Import failed: " + ex.Message, "Import", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                ImportButton.IsEnabled = true;
            }
        }

        private async void PushButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = Grid.SelectedItems.Cast<Employee>()
                .Where(x => !string.IsNullOrWhiteSpace(x.EnrollNumber)).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show(Window.GetWindow(this), "Select one or more employees (with an Enroll #) first.", "Push", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show(Window.GetWindow(this),
                    $"Create/update {selected.Count} user record(s) on the device?\n" +
                    "(Names/IDs only — fingerprints are enrolled at the device.)",
                    "Push to Device", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            PushButton.IsEnabled = false;
            try
            {
                int ok = 0;
                foreach (var emp in selected)
                {
                    var du = new DeviceUser { EnrollNumber = emp.EnrollNumber, Name = emp.Name, Privilege = 0, Enabled = true };
                    if (await Task.Run(() => _device.PushUser(_machineNumber, du))) ok++;
                }
                StatusText.Text = $"Pushed {ok}/{selected.Count} user record(s) to the device.";
            }
            finally { PushButton.IsEnabled = true; }
        }

        private async void RemoveDeviceButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = Grid.SelectedItems.Cast<Employee>()
                .Where(x => !string.IsNullOrWhiteSpace(x.EnrollNumber)).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show(Window.GetWindow(this), "Select one or more employees first.", "Remove", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show(Window.GetWindow(this),
                    $"PERMANENTLY remove {selected.Count} user(s) AND their fingerprints from the device?\n\n" +
                    "This cannot be undone. (Their employee record in this app is kept.)",
                    "Remove from Device", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            RemoveDeviceButton.IsEnabled = false;
            try
            {
                int ok = 0;
                foreach (var emp in selected)
                    if (await Task.Run(() => _device.DeleteUser(_machineNumber, emp.EnrollNumber))) ok++;
                StatusText.Text = $"Removed {ok}/{selected.Count} user(s) from the device.";
            }
            finally { RemoveDeviceButton.IsEnabled = true; }
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            var emp = new Employee { Active = true };
            _employees.Add(emp);
            Grid.SelectedItem = emp;
            Grid.ScrollIntoView(emp);
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = Grid.SelectedItems.Cast<Employee>().ToList();
            if (selected.Count == 0) return;

            if (MessageBox.Show(Window.GetWindow(this), $"Delete {selected.Count} employee(s)?", "Confirm",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            foreach (var emp in selected)
            {
                if (emp.Id > 0) _db.DeleteEmployee(emp.Id);
                _employees.Remove(emp);
            }
            StatusText.Text = $"{_employees.Count} employees.";
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            Grid.CommitEdit(); // flush any in-progress cell edit
            int inserted = 0, updated = 0;
            try
            {
                foreach (var emp in _employees.ToList())
                {
                    // Ignore a pristine, never-saved blank row (e.g. an accidental "Add").
                    if (emp.Id == 0 && string.IsNullOrWhiteSpace(emp.EnrollNumber) && string.IsNullOrWhiteSpace(emp.Name))
                    {
                        _employees.Remove(emp);
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(emp.EnrollNumber))
                        throw new InvalidOperationException("Every employee needs an Enroll # (the device user ID).");

                    if (emp.ShiftId == NoShiftId) emp.ShiftId = null;   // "(None)" → NULL in DB

                    if (emp.Id == 0) { emp.Id = _db.InsertEmployee(emp); inserted++; }
                    else { _db.UpdateEmployee(emp); updated++; }

                    if (emp.ShiftId == null) emp.ShiftId = NoShiftId;   // restore sentinel for display
                }
                StatusText.Text = $"Saved — {inserted} added, {updated} updated.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), "Save failed: " + ex.Message +
                    "\n(Enroll # must be unique.)", "Save", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
