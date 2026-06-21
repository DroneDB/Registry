using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using Newtonsoft.Json;
using Registry.Common;

namespace Registry.Web.Data.Models;

/// <summary>
/// EF entity representing a dataset within an organization.
/// </summary>
public class Dataset : IRequestAccess
{
    /// <summary>Human-readable slug identifier (max 128 chars).</summary>
    [MaxLength(128)]
    [Required]
    public string Slug { get; set; }

    /// <summary>Unique GUID reference for the dataset storage folder.</summary>
    public Guid InternalRef { get; set; }

    /// <summary>Auto-incrementing primary key.</summary>
    [Key]
    public int Id { get; set; }

    /// <summary>Date and time when the dataset was created.</summary>
    [Required]
    public DateTime CreationDate { get; set; }

    /// <summary>Array of file types present in the dataset (e.g., "image", "ortho", "pointcloud").</summary>
    public string[] FileTypes { get; set; }

    /// <summary>Parent organization this dataset belongs to.</summary>
    [Required]
    public Organization Organization { get; set; }

    /// <summary>Collection of upload batches associated with this dataset.</summary>
    public virtual ICollection<Batch> Batches { get; set; }

}