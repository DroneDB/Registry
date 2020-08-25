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
    public class DdbPackageProvider : IDdbPackageProvider
    {

        public string DdbPath { get; }
        public PackageVersion ExpectedVersion { get; }

        private const int MaxWaitTime = 5000;

        public DdbPackageProvider(string ddbPath, PackageVersion expectedVersion)
        {
            DdbPath = ddbPath;
            ExpectedVersion = expectedVersion;
        }

        public bool IsDdbReady(bool ignoreVersion = false)
        {

            var path = Path.Combine(DdbPath, GetExeName());

            if (!File.Exists(path))
                return false;

            try
            {

                var proc = new Process
                {
                    StartInfo = new ProcessStartInfo(path, "--version")
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

                var version = new PackageVersion(proc.StandardOutput.ReadToEnd());

                Debug.WriteLine("DroneDB version: " + version);

                if (!ignoreVersion && version != ExpectedVersion)
                    throw new InvalidOperationException($"Need ddb version {ExpectedVersion}, but found {version}");

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
            { "windows", "ddbcmd.exe"},
            { "linux", "ddb"},
            { "macosx", "ddb"}
        };

        public static string GetExeName()
        {
            return DdbExeNameMapper.SafeGetValue(OperatingSystemInfo.PlatformName) ?? "ddb";
        }
    }
}
