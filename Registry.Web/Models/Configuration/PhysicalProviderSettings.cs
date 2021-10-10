using System.ComponentModel.DataAnnotations;

namespace Registry.Web.Models.Configuration
{
    public class PhysicalProviderSettings
    {
        [Required]
        public string Path { get; set; }
    }
}