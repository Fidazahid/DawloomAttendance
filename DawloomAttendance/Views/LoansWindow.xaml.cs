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
                LedgerGrid.ItemsSource = null;
                DetailHeader.Text = "Select an employee to see their loans";
                return;
            }
            var sum = MasterGrid.SelectedItem as LoanSummary;
            DetailHeader.Text = $"Loans for {enroll} — {sum?.Name}   (outstanding {sum?.Outstanding:0})";
            DetailGrid.ItemsSource = _db.GetLoans(enroll);
            LedgerGrid.ItemsSource = _db.GetLoanLedger(enroll);
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
            _db.RecalculateEmployeeLoans(enroll);   // apply to any already-generated months (back-dated loans)
            ReloadMaster();
            SelectEnroll(enroll);
            StatusText.Text = $"Added loan of {loan.Amount:0} for {enroll}.";
        }

        private void EditLoanButton_Click(object sender, RoutedEventArgs e)
        {
            if (!(DetailGrid.SelectedItem is Loan loan))
            {
                MessageBox.Show(Window.GetWindow(this), "Select a loan to edit.", "Edit Loan",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new LoanEditWindow(loan) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true) return;

            var updated = dlg.Result;               // carries the loan's Id + EnrollNumber
            _db.UpdateLoan(updated);
            _db.RecalculateEmployeeLoans(updated.EnrollNumber);
            ReloadMaster();
            SelectEnroll(updated.EnrollNumber);
            StatusText.Text = $"Edited loan and recalculated {updated.EnrollNumber}.";
        }

        private void DeleteLoanButton_Click(object sender, RoutedEventArgs e)
        {
            if (!(DetailGrid.SelectedItem is Loan loan))
            {
                MessageBox.Show(Window.GetWindow(this), "Select a loan to delete.", "Delete Loan",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string warn = $"Delete this loan of {loan.Amount:0}?";
            if (loan.Deducted > 0)
                warn += "\n\nIt has already been deducted on generated salary slips — those months " +
                        "will be recalculated (loan removed, net pay adjusted).";
            if (MessageBox.Show(Window.GetWindow(this), warn, "Delete Loan",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            var enroll = loan.EnrollNumber;
            _db.DeleteLoan(loan.Id);
            _db.RecalculateEmployeeLoans(enroll);
            ReloadMaster();
            SelectEnroll(enroll);
            StatusText.Text = $"Deleted loan and recalculated {enroll}.";
        }
    }
}
