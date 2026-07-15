using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DawloomAttendance.Data;
using DawloomAttendance.Data.Entities;

namespace DawloomAttendance.Views
{
    /// <summary>
    /// Loans: a master list of employees with their total/outstanding loan, and — for the
    /// selected employee — their individual loans, with Add/Delete. Loans are deducted from
    /// generated salary slips (one-time or by installment).
    /// </summary>
    public partial class LoansWindow : UserControl
    {
        private readonly AppDb _db;

        public LoansWindow(AppDb db)
        {
            InitializeComponent();
            _db = db;
            ReloadMaster();
        }

        private string SelectedEnroll => (MasterGrid.SelectedItem as LoanSummary)?.EnrollNumber;

        private void ReloadMaster()
        {
            var prev = SelectedEnroll;
            MasterGrid.ItemsSource = _db.GetLoanSummaries();
            StatusText.Text = $"{(MasterGrid.ItemsSource as IEnumerable<LoanSummary>)?.Count() ?? 0} employees.";
            if (prev != null) SelectEnroll(prev);
        }

        private void ReloadDetail()
        {
            var enroll = SelectedEnroll;
            if (string.IsNullOrEmpty(enroll))
            {
                DetailGrid.ItemsSource = null;
                DetailHeader.Text = "Select an employee to see their loans";
                return;
            }
            var sum = MasterGrid.SelectedItem as LoanSummary;
            DetailHeader.Text = $"Loans for {enroll} — {sum?.Name}   (outstanding {sum?.Outstanding:0})";
            DetailGrid.ItemsSource = _db.GetLoans(enroll);
        }

        private void SelectEnroll(string enroll)
        {
            var match = (MasterGrid.ItemsSource as IEnumerable<LoanSummary>)?
                .FirstOrDefault(x => x.EnrollNumber == enroll);
            if (match != null) MasterGrid.SelectedItem = match;   // fires SelectionChanged → ReloadDetail
        }

        private void MasterGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => ReloadDetail();

        private void RefreshButton_Click(object sender, RoutedEventArgs e) => ReloadMaster();

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            var enroll = SelectedEnroll;
            if (string.IsNullOrEmpty(enroll))
            {
                MessageBox.Show(Window.GetWindow(this), "Select an employee first.", "Add Loan",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new LoanEditWindow { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true) return;

            var loan = dlg.Result;
            loan.EnrollNumber = enroll;
            _db.InsertLoan(loan);
            ReloadMaster();
            SelectEnroll(enroll);
            StatusText.Text = $"Added loan of {loan.Amount:0} for {enroll}.";
        }

        private void DeleteLoanButton_Click(object sender, RoutedEventArgs e)
        {
            if (!(DetailGrid.SelectedItem is Loan loan))
            {
                MessageBox.Show(Window.GetWindow(this), "Select a loan to delete.", "Delete Loan",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (loan.Deducted > 0)
            {
                MessageBox.Show(Window.GetWindow(this),
                    "This loan has already been (partly) deducted on a salary slip and cannot be deleted.",
                    "Delete Loan", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (MessageBox.Show(Window.GetWindow(this), $"Delete this loan of {loan.Amount:0}?", "Delete Loan",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            var enroll = loan.EnrollNumber;
            _db.DeleteLoan(loan.Id);
            ReloadMaster();
            SelectEnroll(enroll);
        }
    }
}
