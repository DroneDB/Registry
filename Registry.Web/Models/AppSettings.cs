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
        public StorageProvider StorageProvider { get; set; }
        public AdminInfo DefaultAdmin { get; set; }
    }

    public class StorageProvider
    {
        public StorageType Type { get; set; }
        public Dictionary<string, string> Settings { get; set; }
    }

    public class AdminInfo
    {
        public string Email { get; set; }
        public string UserName { get; set; }
        public string Password { get; set; }
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
