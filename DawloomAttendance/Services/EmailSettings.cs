using System.Configuration;

namespace DawloomAttendance.Services
{
    /// <summary>
    /// SMTP configuration + subject templates for outgoing reports, persisted to the
    /// app config (like <see cref="Device.DeviceSettings"/>) so each deployment sets its own.
    /// Subjects support the placeholders {Name} (employee) and {Period} (e.g. a date range).
    /// </summary>
    public class EmailSettings
    {
        public string Host { get; set; } = "";
        public int Port { get; set; } = 587;
        public bool UseSsl { get; set; } = true;
        public string FromAddress { get; set; } = "";
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";

        /// <summary>When false, the app never sends on its own (Monday/1st auto-send is off).</summary>
        public bool EnableAutoSend { get; set; } = false;

        public string SubjectWeekly { get; set; } = "Weekly Attendance Report — {Name} ({Period})";
        public string SubjectMonthly { get; set; } = "Monthly Attendance Report — {Name} ({Period})";
        public string SubjectManual { get; set; } = "Attendance Report — {Name} ({Period})";
        public string SubjectSlip { get; set; } = "Salary Slip — {Name} ({Period})";

        // Sender display name varies by the email's nature (set here, not user-configured).
        public string FromNameReports { get; set; } = "Dawloom Attendance";
        public string FromNameSalary { get; set; } = "Dawloom Salary";

        /// <summary>True when enough is set to attempt a send (host + from address).</summary>
        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress);

        public static EmailSettings Load()
        {
            var s = new EmailSettings();
            var app = ConfigurationManager.AppSettings;

            string v;
            if (!string.IsNullOrWhiteSpace(v = app["Email.Host"])) s.Host = v;
            if (int.TryParse(app["Email.Port"], out var port)) s.Port = port;
            if (bool.TryParse(app["Email.UseSsl"], out var ssl)) s.UseSsl = ssl;
            if (!string.IsNullOrWhiteSpace(v = app["Email.FromAddress"])) s.FromAddress = v;
            if (!string.IsNullOrWhiteSpace(v = app["Email.Username"])) s.Username = v;
            if (!string.IsNullOrWhiteSpace(v = app["Email.Password"])) s.Password = v;
            if (bool.TryParse(app["Email.EnableAutoSend"], out var auto)) s.EnableAutoSend = auto;
            if (!string.IsNullOrWhiteSpace(v = app["Email.SubjectWeekly"])) s.SubjectWeekly = v;
            if (!string.IsNullOrWhiteSpace(v = app["Email.SubjectMonthly"])) s.SubjectMonthly = v;
            if (!string.IsNullOrWhiteSpace(v = app["Email.SubjectManual"])) s.SubjectManual = v;
            if (!string.IsNullOrWhiteSpace(v = app["Email.SubjectSlip"])) s.SubjectSlip = v;

            return s;
        }

        public void Save()
        {
            var cfg = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
            Set(cfg, "Email.Host", Host);
            Set(cfg, "Email.Port", Port.ToString());
            Set(cfg, "Email.UseSsl", UseSsl.ToString());
            Set(cfg, "Email.FromAddress", FromAddress);
            Set(cfg, "Email.Username", Username);
            Set(cfg, "Email.Password", Password);
            Set(cfg, "Email.EnableAutoSend", EnableAutoSend.ToString());
            Set(cfg, "Email.SubjectWeekly", SubjectWeekly);
            Set(cfg, "Email.SubjectMonthly", SubjectMonthly);
            Set(cfg, "Email.SubjectManual", SubjectManual);
            Set(cfg, "Email.SubjectSlip", SubjectSlip);
            cfg.Save(ConfigurationSaveMode.Modified);
            ConfigurationManager.RefreshSection("appSettings");
        }

        private static void Set(Configuration cfg, string key, string value)
        {
            var settings = cfg.AppSettings.Settings;
            if (settings[key] == null) settings.Add(key, value ?? "");
            else settings[key].Value = value ?? "";
        }
    }
}
