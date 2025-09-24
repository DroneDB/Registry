#nullable enable
using System;
using System.ComponentModel.DataAnnotations;

namespace Registry.Web.Data.Models;

public class JobIndex
{
    [Key]
    public string JobId { get; set; } = null!; // PK (string)

    public string OrgSlug { get; set; } = null!;
    public string DsSlug { get; set; } = null!;
    public string? Path { get; set; }
    public string? UserId { get; set; }

    public string? Queue { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastStateChangeUtc { get; set; }

    public string CurrentState { get; set; } = "Created";

    public string? MethodDisplay { get; set; }

    public DateTime? ProcessingAtUtc { get; set; }
    public DateTime? SucceededAtUtc { get; set; }
    public DateTime? FailedAtUtc { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public DateTime? ScheduledAtUtc { get; set; }
}