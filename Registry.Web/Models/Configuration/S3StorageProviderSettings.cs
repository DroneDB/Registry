using System.ComponentModel.DataAnnotations;

namespace Registry.Web.Models.Configuration
{
    public class S3StorageProviderSettings : StorageProviderSettings
    {
        [Required]
        public string Endpoint { get; set; }
        [Required]
        public string AccessKey { get; set; }
        [Required]
        public string SecretKey { get; set; }
        public string Region { get; set; }
        public string SessionToken { get; set; }
        public bool? UseSsl { get; set; }
        public string AppName { get; set; }
        public string AppVersion { get; set; }
        [Required]
        public string BridgeUrl { get; set; }
    }
}