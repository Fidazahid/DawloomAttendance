using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using DawloomAttendance.Data;
using DawloomAttendance.Data.Entities;

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
        private ObservableCollection<RawPunch> _punches;

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
            _punches = new ObservableCollection<RawPunch>(_db.GetPunchesForEmployeeDate(_enroll, _date));
            Grid.ItemsSource = _punches;
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            var selected = Grid.SelectedItems.Cast<RawPunch>().ToList();
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
                    _db.UpdatePunchState(p.Id, p.AttState);
                Changed = true;
                DialogResult = true;   // closes; caller recomputes
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Save failed: " + ex.Message, "Save", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private class AttStateOption
        {
            public int Value { get; set; }
            public string Label { get; set; }
        }
    }
}
