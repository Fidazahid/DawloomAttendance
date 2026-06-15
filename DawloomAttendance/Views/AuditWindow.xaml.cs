using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DawloomAttendance.Data;

namespace DawloomAttendance.Views
{
    /// <summary>Read-only viewer for the audit trail: who changed what, when (date + user filter).</summary>
    public partial class AuditWindow : UserControl
    {
        private readonly AppDb _db;
        private bool _loading;

        private const string AllUsers = "(All users)";

        public AuditWindow(AppDb db)
        {
            InitializeComponent();
            _db = db;
            Loaded += (_, __) => InitOnce();
        }

        private void InitOnce()
        {
            _loading = true;
            ToDate.SelectedDate = DateTime.Today;
            FromDate.SelectedDate = DateTime.Today.AddDays(-30);

            var actors = new[] { AllUsers }.Concat(_db.GetAuditActors()).ToList();
            ActorBox.ItemsSource = actors;
            ActorBox.SelectedIndex = 0;
            _loading = false;

            Reload();
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            // Re-read the actor list too — new users may have acted since the view opened.
            var selected = ActorBox.SelectedItem as string;
            _loading = true;
            ActorBox.ItemsSource = new[] { AllUsers }.Concat(_db.GetAuditActors()).ToList();
            ActorBox.SelectedItem = ((System.Collections.Generic.IEnumerable<string>)ActorBox.ItemsSource)
                .Contains(selected) ? selected : AllUsers;
            _loading = false;
            Reload();
        }

        private void Filter_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_loading) Reload();
        }

        private void Reload()
        {
            if (FromDate.SelectedDate == null || ToDate.SelectedDate == null) return;

            var from = FromDate.SelectedDate.Value;
            var to = ToDate.SelectedDate.Value;
            string actor = ActorBox.SelectedItem as string;
            if (actor == AllUsers) actor = null;

            var rows = _db.GetAuditLog(from, to, actor);
            Grid.ItemsSource = rows;
            Summary.Text = $"{rows.Count} change(s) from {from:yyyy-MM-dd} to {to:yyyy-MM-dd}"
                + (actor == null ? "" : $" by {actor}") + ".";
        }
    }
}
