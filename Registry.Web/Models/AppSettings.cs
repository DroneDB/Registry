using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Registry.Common;

namespace Registry.Web.Models
{
    public class AppSettings
    {
        /// <summary>
        /// Secret to generate JWT tokens
        /// </summary>
        public string Secret { get; set; }

        /// <summary>
        /// JWT token expiration in days
        /// </summary>
        public int TokenExpirationInDays { get; set; }

        /// <summary>
        /// List of JWT revoked tokens
        /// </summary>
        public string[] RevokedTokens { get; set; }

        /// <summary>
        /// Provider for authentication database
        /// </summary>
        public DbProvider AuthProvider { get; set; }

        /// <summary>
        /// Provider for registry database
        /// </summary>
        public DbProvider RegistryProvider { get; set; }

        /// <summary>
        /// Storage provider settings
        /// </summary>
        public StorageProvider StorageProvider { get; set; }

        /// <summary>
        /// Default admin details
        /// </summary>
        public AdminInfo DefaultAdmin { get; set; }

        /// <summary>
        /// Storage path for Ddb databases
        /// </summary>
        public string DdbStoragePath { get; set; }

        /// <summary>
        /// Supported Ddb version
        /// </summary>
        public PackageVersion SupportedDdbVersion { get; set; }

        /// <summary>
        /// Max request body size
        /// </summary>
        public long MaxRequestBodySize { get; set; }

        /// <summary>
        /// Max upload chunk size
        /// </summary>
        public long MaxUploadChunkSize { get; set; }

        /// <summary>
        /// Lenght of batch tokens
        /// </summary>
        public int BatchTokenLength { get; set; }

        /// <summary>
        /// Length of the random generated dataset name
        /// </summary>
        public int RandomDatasetNameLength { get; set; }

        /// <summary>
        /// Temp folder where to store uploaded chunks
        /// </summary>
        public string UploadPath { get; set; }

        /// <summary>
        /// Timeout of the chunked upload sessions
        /// </summary>
        public TimeSpan ChunkedUploadSessionTimeout { get; set; }

    }

    public class StorageProvider
    {
        public StorageType Type { get; set; }
        public DictionaryEx<string, string> Settings { get; set; }
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

    public enum DbProvider
    {
        Sqlite,
        Mysql,
        Mssql
    }
}
