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
        private const string InfoCommand = "info";
        private const string RemoveCommand = "remove";
        private const string InitCommand = "init";
        private const int MaxWaitTime = 5000;

        public Ddb(string ddbExePath)
        {
            _ddbExePath = ddbExePath;
        }


        public IEnumerable<DdbInfo> Info(string path)
        {

            var res = RunCommand($"{InfoCommand} -f json \"{path}\"");

            var lst = JsonConvert.DeserializeObject<DdbInfo[]>(res);

            if (lst == null || lst.Length == 0)
                throw new InvalidOperationException("Cannot parse ddb output");

            return lst;
        }

        public void Remove(string ddbPath, string path)
        {

            var res = RunCommand($"{RemoveCommand} -d \"{ddbPath}\" -p \"{path}\"");
            
            return;
        }

        public void CreateDatabase(string path)
        {

            Directory.CreateDirectory(path);

            var res = RunCommand($"{InitCommand} -d \"{Path.GetFullPath(path)}\"");

        }

        private string RunCommand(string parameters)
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo(_ddbExePath, parameters)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    UseShellExecute = false
                }
            };

            if (!p.StartInfo.EnvironmentVariables.ContainsKey("PROJ_LIB"))
            {
                p.StartInfo.EnvironmentVariables.Add("PROJ_LIB", Path.GetDirectoryName(Path.GetFullPath(_ddbExePath)));
            }

            Debug.WriteLine($"PROJ_LIB = '{p.StartInfo.EnvironmentVariables["PROJ_LIB"]}'");
            Debug.WriteLine("Running command:");
            Debug.WriteLine($"{_ddbExePath} {parameters}");

            p.Start();

            if (!p.WaitForExit(MaxWaitTime))
                throw new IOException("Tried to start ddb process but it's taking too long to complete");

            return p.StandardOutput.ReadToEnd();
        }

    }
}
