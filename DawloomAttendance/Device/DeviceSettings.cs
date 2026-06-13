using System.Configuration;

namespace DawloomAttendance.Device
{
    public class DeviceSettings
    {
        public string Ip { get; set; } = "192.168.1.201";
        public int Port { get; set; } = 4370;
        public int CommKey { get; set; } = 0;
        public int MachineNumber { get; set; } = 1;
        public int ReconnectIntervalSeconds { get; set; } = 10;
        public int EventMask { get; set; } = 65535;
        public int KeepAliveIntervalSeconds { get; set; } = 30;
        public bool SyncTimeOnConnect { get; set; } = true;   // keep the K70 clock matched to the PC

        public static DeviceSettings Load()
        {
            var s = new DeviceSettings();
            var app = ConfigurationManager.AppSettings;

            string v;
            if (!string.IsNullOrWhiteSpace(v = app["Device.Ip"])) s.Ip = v;
            if (int.TryParse(app["Device.Port"], out var port)) s.Port = port;
            if (int.TryParse(app["Device.CommKey"], out var key)) s.CommKey = key;
            if (int.TryParse(app["Device.MachineNumber"], out var mn)) s.MachineNumber = mn;
            if (int.TryParse(app["Device.ReconnectIntervalSeconds"], out var ri)) s.ReconnectIntervalSeconds = ri;
            if (int.TryParse(app["Device.EventMask"], out var em)) s.EventMask = em;
            if (int.TryParse(app["Device.KeepAliveIntervalSeconds"], out var ka)) s.KeepAliveIntervalSeconds = ka;
            if (bool.TryParse(app["Device.SyncTimeOnConnect"], out var st)) s.SyncTimeOnConnect = st;

            return s;
        }

        public void Save()
        {
            var cfg = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
            Set(cfg, "Device.Ip", Ip);
            Set(cfg, "Device.Port", Port.ToString());
            Set(cfg, "Device.CommKey", CommKey.ToString());
            Set(cfg, "Device.MachineNumber", MachineNumber.ToString());
            Set(cfg, "Device.ReconnectIntervalSeconds", ReconnectIntervalSeconds.ToString());
            Set(cfg, "Device.EventMask", EventMask.ToString());
            Set(cfg, "Device.KeepAliveIntervalSeconds", KeepAliveIntervalSeconds.ToString());
            Set(cfg, "Device.SyncTimeOnConnect", SyncTimeOnConnect.ToString());
            cfg.Save(ConfigurationSaveMode.Modified);
            ConfigurationManager.RefreshSection("appSettings");
        }

        private static void Set(Configuration cfg, string key, string value)
        {
            var settings = cfg.AppSettings.Settings;
            if (settings[key] == null) settings.Add(key, value);
            else settings[key].Value = value;
        }
    }
}
