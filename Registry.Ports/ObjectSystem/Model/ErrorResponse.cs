using System;
using System.Collections.Generic;
using System.Text;

namespace Registry.Ports.ObjectSystem.Model
{
    public class ErrorResponse
    {
        public string Code { get; set; }
        public string Message { get; set; }
        public string RequestId { get; set; }
        public string HostId { get; set; }
        public string Resource { get; set; }
        public string BucketName { get; set; }
        public string Key { get; set; }
        public string BucketRegion { get; set; }
    }
}
