#nullable enable
using System.ComponentModel.DataAnnotations;
using Registry.Web.Utilities;

namespace Registry.Web.Models.DTO;

public class MaskBordersRequestDto
{
    [Required]
    public DatasetPath Path { get; set; } = null!;
    public int Near { get; set; } = 15;
    public bool White { get; set; }
}

public class MaskBordersCheckResponseDto
{
    public bool Exists { get; set; }
    public string OutputPath { get; set; } = null!;
}
