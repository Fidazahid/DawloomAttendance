using System;
using System.Windows;
using DawloomAttendance.Data.Entities;

namespace DawloomAttendance.Views
{
    /// <summary>Collects a new loan (or edits an existing one): Date, Type, Payment, optional Installment, Remarks.</summary>
    public partial class LoanEditWindow : Window
    {
        public Loan Result { get; private set; }

        private long? _editId;        // set when editing an existing loan
        private string _editEnroll;

        public LoanEditWindow()
        {
            InitializeComponent();
            DatePick.SelectedDate = DateTime.Today;
        }

        /// <summary>Opens the dialog pre-filled to edit an existing loan.</summary>
        public LoanEditWindow(Loan existing) : this()
        {
            if (existing == null) return;
            Title = "Edit Loan";
            OkButton.Content = "Save";
            _editId = existing.Id;
            _editEnroll = existing.EnrollNumber;

            DatePick.SelectedDate = existing.Date;
            TypeBox.Text = existing.Type ?? string.Empty;
            PaymentBox.Text = existing.Amount.ToString("0.##");
            if (existing.Installment > 0)
            {
                InstallmentCheck.IsChecked = true;           // fires the handler → enables the box
                InstallmentBox.Text = existing.Installment.ToString("0.##");
            }
            RemarksBox.Text = existing.Remarks ?? string.Empty;
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
                Id = _editId ?? 0,
                EnrollNumber = _editEnroll,
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
