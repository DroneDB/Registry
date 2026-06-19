using System;
using System.ComponentModel.DataAnnotations;
using Registry.Common.Model;
using Registry.Ports.DroneDB;

namespace Registry.Web.Data.Models;

/// <summary>
/// EF entity for upload batch entries (path, hash, type, size).
/// </summary>
public class Entry
{
    /// <summary>Auto-incrementing primary key.</summary>
    [Key]
    public int Id { get; set; }

    /// <summary>File path within the dataset.</summary>
    [Required]
    public string Path { get; set; }

    /// <summary>Content hash of the uploaded file.</summary>
    [Required]
    public string Hash { get; set; }

    /// <summary>DroneDB entry type (image, video, pointcloud, etc.).</summary>
    [Required]
    public EntryType Type { get; set; }

    /// <summary>File size in bytes.</summary>
    [Required]
    public long Size { get; set; }

    /// <summary>Date and time when the entry was added.</summary>
    [Required]
    public DateTime AddedOn { get; set; }

    /// <summary>Parent batch this entry belongs to.</summary>
    [Required]
    public Batch Batch { get; set; }
}