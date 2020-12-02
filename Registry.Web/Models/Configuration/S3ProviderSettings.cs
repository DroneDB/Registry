namespace Registry.Web.Models.Configuration
{
    public class S3ProviderSettings : StorageProviderSettings
    {
        public string Endpoint { get; set; }
        public string AccessKey { get; set; }
        public string SecretKey { get; set; }
        public string Region { get; set; }
        public string SessionToken { get; set; }
        public bool? UseSsl { get; set; }
        public string AppName { get; set; }
        public string AppVersion { get; set; }
        public string Location { get; set; }
    }
}