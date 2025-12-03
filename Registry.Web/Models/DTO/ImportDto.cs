using System;

namespace Registry.Web.Models.DTO;

public class ImportDatasetRequestDto
{
    public string SourceRegistryUrl { get; set; }
    public string Username { get; set; }
    public string Password { get; set; }
    public string SourceOrganization { get; set; }
    public string SourceDataset { get; set; }
    public string DestinationOrganization { get; set; }
    public string DestinationDataset { get; set; }
}

public class ImportOrganizationRequestDto
{
    public string SourceRegistryUrl { get; set; }
    public string Username { get; set; }
    public string Password { get; set; }
    public string SourceOrganization { get; set; }
    public string DestinationOrganization { get; set; }
}

public class ImportResultDto
{
    public ImportedItemDto[] ImportedItems { get; set; }
    public ImportErrorDto[] Errors { get; set; }
    public long TotalSize { get; set; }
    public int TotalFiles { get; set; }
    public TimeSpan Duration { get; set; }
}

public class ImportedItemDto
{
    public string Organization { get; set; }
    public string Dataset { get; set; }
    public long Size { get; set; }
    public int FileCount { get; set; }
    public DateTime ImportedAt { get; set; }
}

public enum ImportPhase
{
    Authentication,
    Download,
    Save,
    General
}

public class ImportErrorDto
{
    public string Organization { get; set; }
    public string Dataset { get; set; }
    public string Message { get; set; }
    public ImportPhase Phase { get; set; }
}
