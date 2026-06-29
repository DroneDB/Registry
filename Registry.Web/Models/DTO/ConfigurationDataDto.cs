using System.Collections.Generic;

namespace Registry.Web.Models.DTO;

/// <summary>
/// Root DTO returned by GET /sys/config. Contains all configuration sections and their fields.
/// </summary>
public class ConfigurationDataDto
{
    /// <summary>All configuration sections, ordered by subsystem.</summary>
    public List<ConfigurationSectionDataDto> Sections { get; set; }
}

/// <summary>
/// A logical grouping of related configuration fields (e.g., "Auth & Security", "LDAP").
/// </summary>
public class ConfigurationSectionDataDto
{
    /// <summary>Section key used for JSON nesting (e.g., "Auth", "Storage", "Cache").</summary>
    public string Name { get; set; }

    /// <summary>Display title shown in the UI (e.g., "Authentication & Security").</summary>
    public string Title { get; set; }

    /// <summary>Short description of what this section controls.</summary>
    public string Description { get; set; }

    /// <summary>True if at least one field differs from default or is explicitly configured.</summary>
    public bool IsActive { get; set; }

    /// <summary>Fields belonging to this section.</summary>
    public List<ConfigurationFieldDataDto> Fields { get; set; }
}

/// <summary>
/// A single configuration field with metadata for rendering the correct input type.
/// </summary>
public class ConfigurationFieldDataDto
{
    /// <summary>Field name as it appears in the JSON config (e.g., "TokenExpirationInDays").</summary>
    public string Key { get; set; }

    /// <summary>Human-readable display name (e.g., "Token Expiration").</summary>
    public string DisplayName { get; set; }

    /// <summary>Current value as string. Null for sensitive fields.</summary>
    public string? CurrentValue { get; set; }

    /// <summary>Default value as string. Null for sensitive fields.</summary>
    public string? DefaultValue { get; set; }

    /// <summary>True if field has a non-default/non-null value set. Used for sensitive fields to signal existence without exposing the value.</summary>
    public bool IsSet { get; set; }

    /// <summary>Clear description of what the field does.</summary>
    public string Description { get; set; }

    /// <summary>Input type: "text" | "number" | "bool" | "timespan" | "cron" | "enum" | "password" | "array" | "json".</summary>
    public string FieldType { get; set; }

    /// <summary>Possible enum values (e.g., ["Sqlite", "Mysql"]). Only set when FieldType is "enum".</summary>
    public string[]? EnumOptions { get; set; }

    /// <summary>True for Secret, Password, MonitorToken, connection strings.</summary>
    public bool Sensitive { get; set; }

    /// <summary>Minimum constraint for number fields.</summary>
    public int? MinValue { get; set; }

    /// <summary>Maximum constraint for number fields.</summary>
    public int? MaxValue { get; set; }

    /// <summary>Display unit ("bytes", "days", "seconds", "pixels").</summary>
    public string? Unit { get; set; }
}
