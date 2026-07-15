using System;
using System.Windows;
using DawloomAttendance.Data.Entities;

namespace DawloomAttendance.Views
{
    /// <summary>Collects a new loan: Date, Payment (amount), optional Installment/month, Remarks.</summary>
    public partial class LoanEditWindow : Window
    {
        public Loan Result { get; private set; }

        public LoanEditWindow()
        {
            InitializeComponent();
            DatePick.SelectedDate = DateTime.Today;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (!double.TryParse(PaymentBox.Text, out var amount) || amount <= 0)
            {
                Warn("Enter a valid payment amount (greater than 0).");
                return;
            }

            double installment = 0;
            if (!string.IsNullOrWhiteSpace(InstallmentBox.Text) &&
                (!double.TryParse(InstallmentBox.Text, out installment) || installment < 0))
            {
                Warn("Installment must be a number (or blank for a one-time deduction).");
                return;
            }

            Result = new Loan
            {
                Date = DatePick.SelectedDate ?? DateTime.Today,
                Amount = amount,
                Installment = installment,
                Remarks = string.IsNullOrWhiteSpace(RemarksBox.Text) ? null : RemarksBox.Text.Trim()
            };
            DialogResult = true;
        }

        private void Warn(string msg) =>
            MessageBox.Show(this, msg, "Add Loan", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
