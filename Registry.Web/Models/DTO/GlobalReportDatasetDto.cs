using System;
using System.Collections.Generic;

namespace Registry.Web.Models.DTO;

public class GlobalReportDatasetDto
{
    public string Name { get; set; }
    public string Slug { get; set; }
    public DateTime CreationDate { get; set; }
    public string Description { get; set; }
    public bool IsPublic { get; set; }
    public string Owner { get; set; }
    public long Size { get; set; }
    public List<GlobalReportContentDto> Contents { get; set; } = new();
    public string Error { get; set; }
}
