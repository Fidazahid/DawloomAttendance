using System;
using System.IO;
using Serilog;
using Serilog.Events;

namespace DawloomAttendance.Services
{
    /// <summary>
    /// Configures the rolling-file log sink. This is the on-disk half of the plan's
    /// two-sink logging; the SQLite <c>DeviceLog</c> table (written via AppDb) is the
    /// other half. Files roll daily as logs\dawloom-YYYYMMDD.log and are kept ~1 month.
    /// </summary>
    public static class LoggingSetup
    {
        public static string LogDirectory { get; private set; }

        public static void Init()
        {
            LogDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DawloomAttendance", "logs");
            Directory.CreateDirectory(LogDirectory);

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(
                    path: Path.Combine(LogDirectory, "dawloom-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 31,
                    shared: true,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            Log.Information("=== Dawloom Attendance started ===");
        }

        public static void Shutdown()
        {
            Log.Information("=== Dawloom Attendance stopped ===");
            Log.CloseAndFlush();
        }

        /// <summary>Maps the app's text level ("Info"/"Warn"/"Error") to a Serilog level.</summary>
        public static LogEventLevel Map(string level)
        {
            switch (level)
            {
                case "Error": return LogEventLevel.Error;
                case "Warn":  return LogEventLevel.Warning;
                default:      return LogEventLevel.Information;
            }
        }
    }
}
