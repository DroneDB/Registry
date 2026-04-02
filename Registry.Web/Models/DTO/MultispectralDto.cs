#nullable enable
using System.Collections.Generic;

namespace Registry.Web.Models.DTO;

/// <summary>
/// DTO for raster sensor information response
/// </summary>
public class RasterInfoDto
{
    public int BandCount { get; set; }
    public string? DataType { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string? DetectedSensor { get; set; }
    public List<BandInfoDto> Bands { get; set; } = new();
    public List<PresetDto> AvailablePresets { get; set; } = new();
    public string? DefaultPreset { get; set; }
}

public class BandInfoDto
{
    public int Index { get; set; }
    public string? Name { get; set; }
    public double? Wavelength { get; set; }
    public string? ColorInterpretation { get; set; }
}

public class PresetDto
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public BandMappingDto? BandMapping { get; set; }
    public bool IsDefault { get; set; }
}

public class BandMappingDto
{
    public int R { get; set; }
    public int G { get; set; }
    public int B { get; set; }
}

/// <summary>
/// DTO for merge-multispectral request
/// </summary>
public class MergeMultispectralRequestDto
{
    public string[] Paths { get; set; } = [];
    public string? PreviewBands { get; set; }
    public int? ThumbSize { get; set; }
    public string? OutputPath { get; set; }
}

/// <summary>
/// DTO for merge-multispectral validation response
/// </summary>
public class MergeValidationResultDto
{
    public bool Ok { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public MergeSummaryDto? Summary { get; set; }
}

public class MergeSummaryDto
{
    public int TotalBands { get; set; }
    public string? DataType { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string? Crs { get; set; }
    public double PixelSizeX { get; set; }
    public double PixelSizeY { get; set; }
    public long EstimatedSize { get; set; }
}
