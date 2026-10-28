#nullable disable
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
    public string Slug { get; set; }
    [Required]
    public DateTime CreationDate { get; set; }
    public Dictionary<string, JsonElement> Properties { get; set; }

    public long Size { get; set; }

    public DatasetPermissionsDto Permissions { get; set; }

    public bool? HasThumbnail { get; set; }

}

public class DatasetNewDto
{
    [Required]
    public string Slug { get; set; }

    public string Name { get; set; }

    public Visibility? Visibility { get; set; }

    [MaxLength(256)]
    public string Tagline { get; set; }
}

public class DatasetEditDto
{

    public string Name { get; set; }

    public Visibility? Visibility { get; set; }

}