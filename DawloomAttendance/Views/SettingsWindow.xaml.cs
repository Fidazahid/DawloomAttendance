using System.Windows;
using DawloomAttendance.Device;

namespace DawloomAttendance.Views
{
    /// <summary>
    /// Modal dialog for editing <see cref="DeviceSettings"/>. On Save it validates
    /// the fields and exposes the edited settings via <see cref="Result"/>; the caller
    /// persists them. Lets the customer re-point at a different K70 without a rebuild.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        /// <summary>The validated settings, set only when the dialog returns true.</summary>
        public DeviceSettings Result { get; private set; }

        public SettingsWindow(DeviceSettings current)
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
        }

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
            DialogResult = true;
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
