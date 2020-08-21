using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Registry.Adapters.DroneDB.Models;
using Registry.Ports.DroneDB;
using Registry.Ports.DroneDB.Models;

namespace Registry.Adapters.DroneDB
{
    public class Ddb : IDdb
    {
        private readonly string _ddbExePath;

        // TODO: Maybe all this "stuff" can be put in the config
        private const string InfoCommand = "info -f json";
        private const int MaxWaitTime = 5000;

        public Ddb(string ddbExePath)
        {

            if (!File.Exists(ddbExePath))
                throw new ArgumentException("Cannot find ddb executable");

            _ddbExePath = ddbExePath;
        }

        public IEnumerable<DdbInfo> Info(string path)
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo(_ddbExePath, $"{InfoCommand} \"{path}\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    UseShellExecute = false
                }
            };

            p.StartInfo.EnvironmentVariables.Add("PROJ_LIB", Path.GetDirectoryName(Path.GetFullPath(_ddbExePath)));

            Debug.WriteLine("Running command:");
            Debug.WriteLine($"{Path.GetFullPath(_ddbExePath)} {p.StartInfo.Arguments}");

            p.Start();

            if (!p.WaitForExit(MaxWaitTime))
                throw new IOException("Tried to start ddb process but it's taking too long to complete");

            var res = p.StandardOutput.ReadToEnd();

            var lst = JsonConvert.DeserializeObject<DdbInfo[]>(res);

            if (lst == null || lst.Length == 0)
                throw new InvalidOperationException("Cannot parse ddb output");

            return lst;
        }
    }
}
