#nullable enable
using System;

namespace Registry.Web.Models.DTO;

public class BuildJobDto
{
    public string JobId { get; set; } = null!;
    public string? Path { get; set; }
    public string CurrentState { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessingAt { get; set; }
    public DateTime? SucceededAt { get; set; }
    public DateTime? FailedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}