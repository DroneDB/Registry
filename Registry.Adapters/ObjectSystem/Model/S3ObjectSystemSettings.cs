using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Registry.Adapters.ObjectSystem.Model
{
    public class S3ObjectSystemSettings
    {
        public string Endpoint { get; set; }
        public string AccessKey { get; set; }
        public string SecretKey { get; set; }
        public string Region { get; set; }
        public string SessionToken { get; set; }
        public bool UseSsl { get; set; }
        public string AppName { get; set; }
        public string AppVersion { get; set; }
    }
}
