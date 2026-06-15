using System;
using System.Collections.Generic;
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
        private bool _initialized;

        private const string AllUsers = "(All users)";

        public AuditWindow(AppDb db)
        {
            InitializeComponent();
            _db = db;
            Loaded += OnLoaded;
            // Auto-refresh whenever the tab is shown again, so new changes appear without
            // anyone clicking Refresh (the manual button stays as a force-refresh).
            IsVisibleChanged += OnVisibleChanged;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_initialized) return;
            _loading = true;
            ToDate.SelectedDate = DateTime.Today;
            FromDate.SelectedDate = DateTime.Today.AddDays(-30);
            _loading = false;
            _initialized = true;
            RefreshActorsAndReload();
        }

        private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_initialized && IsVisible) RefreshActorsAndReload();
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshActorsAndReload();

        private void Filter_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_loading) Reload();
        }

        /// <summary>Repopulates the user filter (new actors may have appeared) and reloads the grid.</summary>
        private void RefreshActorsAndReload()
        {
            var selected = ActorBox.SelectedItem as string;
            _loading = true;
            var actors = new List<string> { AllUsers };
            actors.AddRange(_db.GetAuditActors());
            ActorBox.ItemsSource = actors;
            ActorBox.SelectedItem = actors.Contains(selected) ? selected : AllUsers;
            _loading = false;
            Reload();
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
