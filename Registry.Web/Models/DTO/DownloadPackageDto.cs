using System;

namespace Registry.Web.Models.DTO
{
    public class DownloadPackageDto
    {
        public string DownloadUrl { get; set; }
        public DateTime? Expiration { get; set; }
    }
}