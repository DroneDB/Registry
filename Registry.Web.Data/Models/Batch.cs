using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Text;

namespace Registry.Web.Data.Models;

/// <summary>
/// EF entity for upload batches (token, user, status, entries).
/// </summary>
public class Batch
{
    /// <summary>Random token identifier for the batch (primary key).</summary>
    [Key]
    public string Token { get; set; }

    /// <summary>Dataset this batch is uploading to.</summary>
    [Required]
    public Dataset Dataset { get; set; }

    /// <summary>Username of the user who initiated the batch.</summary>
    [Required]
    public string UserName { get; set; }

    /// <summary>Batch start timestamp.</summary>
    [Required]
    public DateTime Start { get; set; }

    /// <summary>Batch completion timestamp (null if still running).</summary>
    public DateTime? End { get; set; }

    /// <summary>Current batch status (Running, Committed, Rolledback).</summary>
    public BatchStatus Status { get; set; }

    /// <summary>Collection of file entries in this batch.</summary>
    public virtual ICollection<Entry> Entries { get; set; }
}

/// <summary>
/// Upload batch lifecycle status.
/// </summary>
public enum BatchStatus
{
    /// <summary>Batch is currently being uploaded.</summary>
    Running,
    /// <summary>Batch was successfully committed to the dataset.</summary>
    Committed,
    /// <summary>Batch was rolled back (files removed).</summary>
    Rolledback
}