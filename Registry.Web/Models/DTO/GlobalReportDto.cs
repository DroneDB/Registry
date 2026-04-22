using System.Collections.Generic;

namespace Registry.Web.Models.DTO;

public class GlobalReportDto
{
    public string UserName { get; set; }
    public List<GlobalReportOrganizationDto> Organizations { get; set; } = new();
}
