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

        // Enable the installment field only when installments are chosen.
        private void InstallmentCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (InstallmentBox == null) return;   // fires once during InitializeComponent
            bool on = InstallmentCheck.IsChecked == true;
            InstallmentBox.IsEnabled = on;
            InstallmentHint.Text = on
                ? "This amount is deducted from every salary slip until the loan is repaid."
                : "Unchecked: the whole amount is deducted on the next salary slip.";
            if (on) InstallmentBox.Focus();
            else InstallmentBox.Clear();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (!double.TryParse(PaymentBox.Text, out var amount) || amount <= 0)
            {
                Warn("Enter a valid payment amount (greater than 0).");
                return;
            }

            double installment = 0;
            if (InstallmentCheck.IsChecked == true &&
                (!double.TryParse(InstallmentBox.Text, out installment) || installment <= 0))
            {
                Warn("Enter a valid monthly installment (greater than 0), or uncheck installments for a one-time deduction.");
                return;
            }
            if (installment > amount)
            {
                Warn("The monthly installment can't be more than the loan amount.");
                return;
            }

            Result = new Loan
            {
                Date = DatePick.SelectedDate ?? DateTime.Today,
                Type = ReadType(),
                Amount = amount,
                Installment = installment,
                Remarks = string.IsNullOrWhiteSpace(RemarksBox.Text) ? null : RemarksBox.Text.Trim()
            };
            DialogResult = true;
        }

        // The editable combo's Text (a preset or a typed-in value); null if left blank.
        private string ReadType()
        {
            var t = TypeBox.Text;
            return string.IsNullOrWhiteSpace(t) ? null : t.Trim();
        }

        private void Warn(string msg) =>
            MessageBox.Show(this, msg, "Add Loan", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
