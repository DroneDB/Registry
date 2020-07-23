using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Registry.Web.Models
{
    public class AppSettings
    {
        public string Secret { get; set; }
        public int TokenExpirationInDays { get; set; }
        public AuthProvider AuthProvider  {get; set;}
    }

    public class StorageProvider
    {
        public StorageType Type { get; set; }
        public Dictionary<string, string> Settings { get; set; }
    }

    public enum StorageType
    {
        Physical,
        S3
    }

    public enum AuthProvider
    {
        Sqlite,
        Mysql,
        Mssql
    }
}
