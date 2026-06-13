using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DawloomAttendance.Data;
using DawloomAttendance.Services;

namespace DawloomAttendance.Views
{
    /// <summary>Manage local database backups: set folder, back up now, and restore.</summary>
    public partial class BackupView : UserControl
    {
        private readonly AppDb _db;

        public BackupView(AppDb db)
        {
            InitializeComponent();
            _db = db;
            DirBox.Text = BackupService.GetBackupDir(_db);
            Reload();
        }

        private void Reload()
        {
            Grid.ItemsSource = BackupService.ListBackups(_db).Select(f => new BackupRow
            {
                Name = f.Name,
                FullPath = f.FullName,
                SizeText = (f.Length / 1024.0).ToString("0.#") + " KB",
                Created = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
            }).ToList();
            StatusText.Text = $"{BackupService.ListBackups(_db).Count} backup(s).";
        }

        private void SaveDir_Click(object sender, RoutedEventArgs e)
        {
            var dir = (DirBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(dir)) return;
            try
            {
                Directory.CreateDirectory(dir);
                BackupService.SetBackupDir(_db, dir);
                Reload();
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), "Could not set folder: " + ex.Message, "Backup", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenDir_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dir = BackupService.GetBackupDir(_db);
                Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start(dir);
            }
            catch { /* ignore */ }
        }

        private void BackupNow_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var path = BackupService.RunBackup(_db);
                Reload();
                StatusText.Text = "Backed up to " + Path.GetFileName(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), "Backup failed: " + ex.Message, "Backup", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

        private void Restore_Click(object sender, RoutedEventArgs e)
        {
            if (!(Grid.SelectedItem is BackupRow row))
            {
                MessageBox.Show(Window.GetWindow(this), "Select a backup to restore.", "Restore", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show(Window.GetWindow(this),
                    $"Replace the live database with:\n{row.Name}\n\nCurrent data will be overwritten and the app will restart. Continue?",
                    "Restore", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            try
            {
                // Safety: snapshot the current DB before overwriting it.
                BackupService.RunBackup(_db);
                BackupService.Restore(row.FullPath, _db.DbPath);

                MessageBox.Show(Window.GetWindow(this), "Restored. The app will now restart.", "Restore", MessageBoxButton.OK, MessageBoxImage.Information);
                System.Diagnostics.Process.Start(System.Reflection.Assembly.GetEntryAssembly().Location);
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), "Restore failed: " + ex.Message, "Restore", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private class BackupRow
        {
            public string Name { get; set; }
            public string FullPath { get; set; }
            public string SizeText { get; set; }
            public string Created { get; set; }
        }
    }
}
