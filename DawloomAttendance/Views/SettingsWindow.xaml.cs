using System;
using System.Windows;
using DawloomAttendance.Device;
using DawloomAttendance.Services;

namespace DawloomAttendance.Views
{
    /// <summary>
    /// Modal dialog for editing <see cref="DeviceSettings"/> and <see cref="EmailSettings"/>.
    /// On Save it validates the fields and exposes the edited settings via <see cref="Result"/>
    /// and <see cref="EmailResult"/>; the caller persists them.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        /// <summary>The validated device settings, set only when the dialog returns true.</summary>
        public DeviceSettings Result { get; private set; }

        /// <summary>The validated email settings, set only when the dialog returns true.</summary>
        public EmailSettings EmailResult { get; private set; }

        public SettingsWindow(DeviceSettings current, EmailSettings email)
        {
            InitializeComponent();

            IpBox.Text        = current.Ip;
            PortBox.Text      = current.Port.ToString();
            CommKeyBox.Text   = current.CommKey.ToString();
            MachineBox.Text   = current.MachineNumber.ToString();
            ReconnectBox.Text = current.ReconnectIntervalSeconds.ToString();
            KeepAliveBox.Text = current.KeepAliveIntervalSeconds.ToString();
            EventMaskBox.Text = current.EventMask.ToString();
            SyncTimeBox.IsChecked = current.SyncTimeOnConnect;

            email = email ?? new EmailSettings();
            SmtpHostBox.Text    = email.Host;
            SmtpPortBox.Text    = email.Port.ToString();
            SmtpSslBox.IsChecked = email.UseSsl;
            FromAddressBox.Text = email.FromAddress;
            SmtpUserBox.Text    = email.Username;
            SmtpPassBox.Password = email.Password;
            AutoSendBox.IsChecked = email.EnableAutoSend;
            // Subject lines are fixed defaults (not user-configured); _email carries them through.
            _currentEmail = email;
        }

        private EmailSettings _currentEmail;

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            string ip = (IpBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(ip)) { Invalid("IP address is required."); return; }
            if (!TryPort(PortBox.Text, out int port)) { Invalid("Port must be a number between 1 and 65535."); return; }
            if (!int.TryParse(CommKeyBox.Text, out int commKey)) { Invalid("Comm Key must be a whole number."); return; }
            if (!int.TryParse(MachineBox.Text, out int machine)) { Invalid("Machine # must be a whole number."); return; }
            if (!TryPositive(ReconnectBox.Text, out int reconnect)) { Invalid("Reconnect interval must be a positive number of seconds."); return; }
            if (!TryPositive(KeepAliveBox.Text, out int keepAlive)) { Invalid("Keep-alive interval must be a positive number of seconds."); return; }
            if (!int.TryParse(EventMaskBox.Text, out int eventMask)) { Invalid("Event Mask must be a whole number."); return; }

            // Email is optional; only validate the SMTP port format when a host is given.
            int smtpPort = 587;
            if (!string.IsNullOrWhiteSpace(SmtpHostBox.Text) && !TryPort(SmtpPortBox.Text, out smtpPort))
            { Invalid("SMTP port must be a number between 1 and 65535."); return; }

            Result = new DeviceSettings
            {
                Ip = ip,
                Port = port,
                CommKey = commKey,
                MachineNumber = machine,
                ReconnectIntervalSeconds = reconnect,
                KeepAliveIntervalSeconds = keepAlive,
                EventMask = eventMask,
                SyncTimeOnConnect = SyncTimeBox.IsChecked == true
            };
            EmailResult = ReadEmail(smtpPort);
            DialogResult = true;
        }

        private EmailSettings ReadEmail(int smtpPort) => new EmailSettings
        {
            Host = (SmtpHostBox.Text ?? "").Trim(),
            Port = smtpPort,
            UseSsl = SmtpSslBox.IsChecked == true,
            FromAddress = (FromAddressBox.Text ?? "").Trim(),
            Username = (SmtpUserBox.Text ?? "").Trim(),
            Password = SmtpPassBox.Password,
            EnableAutoSend = AutoSendBox.IsChecked == true,
            // Keep the fixed subject templates (we set these, not the user).
            SubjectWeekly = _currentEmail?.SubjectWeekly ?? new EmailSettings().SubjectWeekly,
            SubjectMonthly = _currentEmail?.SubjectMonthly ?? new EmailSettings().SubjectMonthly,
            SubjectManual = _currentEmail?.SubjectManual ?? new EmailSettings().SubjectManual,
            SubjectSlip = _currentEmail?.SubjectSlip ?? new EmailSettings().SubjectSlip
        };

        private void TestEmail_Click(object sender, RoutedEventArgs e)
        {
            if (!TryPort(SmtpPortBox.Text, out int smtpPort)) smtpPort = 587;
            var s = ReadEmail(smtpPort);
            if (!s.IsConfigured) { Invalid("Enter at least the SMTP host and From address first."); return; }

            TestEmailButton.IsEnabled = false;
            try
            {
                EmailService.Send(s, s.FromAddress, "Dawloom Attendance — test email",
                    "This is a test email confirming your SMTP settings work.", null, s.FromNameReports);
                MessageBox.Show(this, "Test email sent to " + s.FromAddress + ".", "Email test",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Test failed: " + ex.Message, "Email test",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                TestEmailButton.IsEnabled = true;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void Invalid(string message)
            => MessageBox.Show(this, message, "Invalid setting", MessageBoxButton.OK, MessageBoxImage.Warning);

        private static bool TryPort(string s, out int value)
            => int.TryParse(s, out value) && value >= 1 && value <= 65535;

        private static bool TryPositive(string s, out int value)
            => int.TryParse(s, out value) && value >= 1;
    }
}
