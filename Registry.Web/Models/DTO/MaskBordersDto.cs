#nullable enable
using System.ComponentModel.DataAnnotations;

namespace Registry.Web.Models.DTO;

public class MaskBordersRequestDto
{
    [Required]
    public string Path { get; set; } = null!;
    public int Near { get; set; } = 15;
    public bool White { get; set; } = false;
}

public class MaskBordersCheckResponseDto
{
    public bool Exists { get; set; }
    public string OutputPath { get; set; } = null!;
}
