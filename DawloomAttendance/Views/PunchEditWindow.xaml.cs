using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using DawloomAttendance.Data;
using DawloomAttendance.Data.Entities;
using DawloomAttendance.Device.Events;

namespace DawloomAttendance.Views
{
    /// <summary>
    /// Review/correct the raw punches for one employee on one day. The common fix:
    /// a user checked in twice, so one of those should be a check-out — switch its Type.
    /// </summary>
    public partial class PunchEditWindow : Window
    {
        private readonly AppDb _db;
        private readonly string _enroll;
        private readonly DateTime _date;
        private ObservableCollection<PunchRow> _punches;

        /// <summary>True if anything was saved/deleted, so the caller should recompute.</summary>
        public bool Changed { get; private set; }

        public PunchEditWindow(AppDb db, string enrollNumber, DateTime date, string name)
        {
            InitializeComponent();
            _db = db;
            _enroll = enrollNumber;
            _date = date;

            TypeColumn.ItemsSource = new List<AttStateOption>
            {
                new AttStateOption { Value = 0, Label = "Check-In" },
                new AttStateOption { Value = 1, Label = "Check-Out" },
                new AttStateOption { Value = 2, Label = "Break-Out" },
                new AttStateOption { Value = 3, Label = "Break-In" },
                new AttStateOption { Value = 4, Label = "Overtime-In" },
                new AttStateOption { Value = 5, Label = "Overtime-Out" },
            };

            HeaderText.Text = $"Enroll #{enrollNumber}  {name}   —   {date:yyyy-MM-dd}";
            Reload();
        }

        private void Reload()
        {
            _punches = new ObservableCollection<PunchRow>(
                _db.GetPunchesForEmployeeDate(_enroll, _date).Select(p => new PunchRow(p)));
            Grid.ItemsSource = _punches;
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            if (!TryParseTimeOfDay(NewTimeBox.Text, out var tod))
            {
                MessageBox.Show(this, "Enter the time as 24-hour HH:mm (e.g. 18:00) or with AM/PM (e.g. 9:00 PM).",
                    "Add punch", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int attState = int.Parse(((System.Windows.Controls.ComboBoxItem)NewTypeBox.SelectedItem).Tag.ToString());
            var ts = _date.Date + tod;

            var punch = new RawPunch
            {
                EnrollNumber = _enroll,
                Timestamp = ts,
                AttState = attState,
                VerifyMethod = 0,
                WorkCode = 0,
                IsValid = true,
                CapturedAt = DateTime.Now,
                Source = "manual"
            };

            if (_db.InsertPunchIfNew(punch))
            {
                Changed = true;
                Reload();
            }
            else
            {
                MessageBox.Show(this, "A punch already exists at that time.", "Add punch", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            var selected = Grid.SelectedItems.Cast<PunchRow>().ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show(this, "Select the punch row(s) to delete.", "Delete", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show(this, $"Delete {selected.Count} punch(es)?", "Confirm",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            foreach (var p in selected)
            {
                _db.DeletePunch(p.Id);
                _punches.Remove(p);
            }
            Changed = true;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            Grid.CommitEdit();
            try
            {
                foreach (var p in _punches)
                {
                    _db.UpdatePunchState(p.Id, p.AttState);
                    if (p.TimeChanged)
                    {
                        // Suppress the device-original time so backfill won't re-add it,
                        // then persist the corrected time.
                        _db.SuppressDevicePunch(p.EnrollNumber, p.OriginalTimestamp, "time-edited");
                        _db.UpdatePunchTime(p.Id, p.Timestamp);
                    }
                }
                Changed = true;
                DialogResult = true;   // closes; caller recomputes
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Save failed: " + ex.Message, "Save", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Parses a punch time. Accepts 24-hour ("18:00", "18:00:30", "9:00" → 09:00) and
        /// 12-hour with an AM/PM designator ("9:00 PM", "9 PM", "9pm" → 21:00). A bare number
        /// without AM/PM is always read as 24-hour, so "9" means 09:00, not 9 PM.
        /// </summary>
        internal static bool TryParseTimeOfDay(string text, out TimeSpan tod)
        {
            tod = default;
            text = (text ?? "").Trim().ToUpperInvariant();
            string[] formats =
            {
                "HH:mm:ss", "H:mm:ss", "HH:mm", "H:mm",
                "h:mm:ss tt", "h:mm tt", "h:mmtt", "h tt", "htt", "hh:mm tt"
            };
            if (DateTime.TryParseExact(text, formats, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var dt))
            {
                tod = dt.TimeOfDay;
                return true;
            }
            return false;
        }

        private class AttStateOption
        {
            public int Value { get; set; }
            public string Label { get; set; }
        }
    }

    /// <summary>
    /// Editable view over a <see cref="RawPunch"/> for the grid. Time is editable: typing
    /// a new value (24-hour HH:mm[:ss] or AM/PM) rewrites the punch's time-of-day while
    /// keeping its date. Invalid text is ignored (the cell reverts to the stored time).
    /// </summary>
    public class PunchRow : INotifyPropertyChanged
    {
        private readonly RawPunch _p;
        private readonly DateTime _originalTs;
        public PunchRow(RawPunch p) { _p = p; _originalTs = p.Timestamp; }

        public long Id => _p.Id;
        public string EnrollNumber => _p.EnrollNumber;
        public DateTime Timestamp => _p.Timestamp;
        public DateTime OriginalTimestamp => _originalTs;
        public bool TimeChanged => _p.Timestamp != _originalTs;
        public string Source => _p.Source;
        public string VerifyMethodLabel => new PunchEvent { VerifyMethod = _p.VerifyMethod }.VerifyMethodLabel;

        public int AttState
        {
            get => _p.AttState;
            set { if (_p.AttState != value) { _p.AttState = value; OnChanged(nameof(AttState)); } }
        }

        /// <summary>The punch time, shown and edited as 24-hour HH:mm:ss.</summary>
        public string TimeText
        {
            get => _p.Timestamp.ToString("HH:mm:ss");
            set
            {
                if (PunchEditWindow.TryParseTimeOfDay(value, out var tod))
                {
                    _p.Timestamp = _p.Timestamp.Date + tod;   // keep the date, replace the time
                    OnChanged(nameof(TimeText));
                }
                else
                {
                    OnChanged(nameof(TimeText));   // revert the cell to the stored value
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
