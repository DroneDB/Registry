using System.Collections.Generic;

namespace Registry.Web.Models.DTO;

public class GlobalReportOrganizationDto
{
    public string Name { get; set; }
    public List<GlobalReportDatasetDto> Datasets { get; set; } = new();
}
