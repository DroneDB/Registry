using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Registry.Ports.DroneDB;

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

}

public class DatasetNewDto
{
    // [ApiController] answers 400 before the action runs when a [Required] member is missing, so it is never null there
    [Required]
    public string Slug { get; set; } = null!;

    public string? Name { get; set; }

    public Visibility? Visibility { get; set; }

    [MaxLength(256)]
    public string? Tagline { get; set; }
}

public class DatasetEditDto
{

    public string? Name { get; set; }

    public Visibility? Visibility { get; set; }

}