using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using Registry.Common;
using Registry.Ports.DroneDB;

namespace Registry.Adapters.DroneDB
{
    public class DdbProvider : IDdbProvider
    {
        public string DdbPath { get; }
        public PackageVersion Version { get; }

        private const int MaxWaitTime = 5000;

        private const string DdbReleaseDownloadUrl =
            "https://github.com/DroneDB/DroneDB/releases/download/v{0}.{1}.{2}/ddb-{0}.{1}.{2}-{3}.zip";

        public DdbProvider(string ddbPath, PackageVersion version)
        {
            DdbPath = ddbPath;
            Version = version;
        }

        public bool IsDdbReady()
        {
            if (!Directory.Exists(DdbPath) || (!File.Exists(Path.Combine(DdbPath, "ddbcmd.exe")) &&
                                               !File.Exists(Path.Combine(DdbPath, "ddb")))) return false;

            try
            {

                var proc = new Process
                {
                    StartInfo = new ProcessStartInfo(Path.Combine(DdbPath, DdbExeNameMapper.SafeGetValue(OperatingSystemInfo.PlatformName)), "--version")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        UseShellExecute = false
                    }
                };

                proc.Start();
                if (!proc.WaitForExit(MaxWaitTime))
                    throw new IOException("Tried to start ddb process but it's taking too long to complete");
                
                Debug.WriteLine("DroneDB version: " + proc.StandardOutput.ReadToEnd());

                return true;

            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error in running ddb: " + ex.Message);
                return false;
            }
        }

        private static readonly Dictionary<string, string> DdbExeNameMapper = new Dictionary<string, string>
        {
            { "windows", "ddb.bat"},
            { "linux", "ddb"},
            { "macosx", "ddb"}
        };

        public static string GetExeName()
        {
            return DdbExeNameMapper.SafeGetValue(OperatingSystemInfo.PlatformName) ?? "ddb";
        }

        public void DownloadDdb()
        {
            Console.WriteLine($" -> Downloading DDB v{Version} in '{DdbPath}'");

            var downloadUrl = string.Format(DdbReleaseDownloadUrl, Version.Major, Version.Minor,
                Version.Build, OperatingSystemInfo.PlatformName);

            using var client = new WebClient();

            var tempPath = Path.GetTempFileName();

            client.DownloadFile(downloadUrl, tempPath);

            Console.WriteLine($" -> Extracting to '{DdbPath}'");

            CommonUtils.SmartExtractFolder(tempPath, DdbPath);

            File.Delete(tempPath);

            Console.WriteLine(" ?> All ok");
        }

        public void EnsureDdb()
        {
            if (IsDdbReady()) return;

            DownloadDdb();
                
            if (!IsDdbReady())
                throw new InvalidOperationException("Downloaded copy of ddb does not work");
        }
    }
}
