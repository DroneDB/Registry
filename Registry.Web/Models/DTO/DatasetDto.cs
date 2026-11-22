using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Registry.Ports.DroneDB;
using Registry.Web.Utilities;

namespace Registry.Web.Models.DTO;

public class DatasetPermissionsDto
{
    public bool CanRead { get; set; }
    public bool CanWrite { get; set; }
    public bool CanDelete { get; set; }
}

public class DatasetDto
{

    [Required]
    public required string Slug { get; set; }
    [Required]
    public DateTime CreationDate { get; set; }
    public Dictionary<string, JsonElement>? Properties { get; set; }

    public long Size { get; set; }

    /// <summary>
    /// When the last change reached the dataset's index (entries or meta; builds do not count). Null when the
    /// index cannot be read. It can trail the change itself by a short time.
    /// </summary>
    public DateTime? LastModified { get; set; }

    public DatasetPermissionsDto? Permissions { get; set; }

    public bool? HasThumbnail { get; set; }

    /// <summary>
    /// Set while a task imports the dataset or rescans its index: until it ends, the dataset can only be deleted. Null
    /// when the dataset is not busy, and in the answer of one dataset (that answer is refused while it is busy).
    /// </summary>
    public DatasetBusyDto? Busy { get; set; }

}

/// <summary>The task that holds a busy dataset (<see cref="DatasetDto.Busy"/>).</summary>
public class DatasetBusyDto
{
    /// <summary>What the task does: <c>importing</c> or <c>rescanning</c>.</summary>
    [Required]
    public required string Kind { get; set; }

    /// <summary>The id of the task (its status, events and cancel are under the dataset's tasks).</summary>
    [Required]
    public required string TaskId { get; set; }

    /// <summary>The progress of the task in percent, or null while it cannot tell.</summary>
    public int? Progress { get; set; }
}

public class DatasetNewDto
{
    // Not [Required]: a name with no Latin letter or digit and no slug is refused for the name (D-035), with its own
    // message, before the missing slug is (DatasetCreation refuses a create without a slug)
    public string? Slug { get; set; }

    [MaxLength(SlugRules.NameMaxLength, ErrorMessage = SlugRules.NameTooLongMessage)]
    public string? Name { get; set; }

    public Visibility? Visibility { get; set; }

    [MaxLength(256)]
    public string? Tagline { get; set; }
}

public class DatasetEditDto
{

    [MaxLength(SlugRules.NameMaxLength, ErrorMessage = SlugRules.NameTooLongMessage)]
    public string? Name { get; set; }

    public Visibility? Visibility { get; set; }

}