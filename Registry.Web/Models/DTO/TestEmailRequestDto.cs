#nullable enable
using System.ComponentModel.DataAnnotations;

namespace Registry.Web.Models.DTO;

/// <summary>Where the test e-mail goes (<c>POST /sys/email/test</c>).</summary>
public sealed class TestEmailRequestDto
{
    /// <summary>The recipient address.</summary>
    [Required]
    public string To { get; set; } = string.Empty;
}
