using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Registry.Web.Data.Models;

/// <summary>
/// EF entity for upload batches (token, user, status, entries).
/// </summary>
public class Batch
{
    /// <summary>Random token identifier for the batch (primary key).</summary>
    [Key]
    public string Token { get; set; } = null!;

    /// <summary>Dataset this batch is uploading to.</summary>
    [Required]
    public Dataset Dataset { get; set; } = null!;

    /// <summary>Username of the user who initiated the batch.</summary>
    [Required]
    public string UserName { get; set; } = null!;

    /// <summary>Batch start timestamp.</summary>
    [Required]
    public DateTime Start { get; set; }

    /// <summary>Batch completion timestamp (null if still running).</summary>
    public DateTime? End { get; set; }

    /// <summary>Current batch status (Running, Committed, Rolledback).</summary>
    public BatchStatus Status { get; set; }

    /// <summary>
    /// The batch created <see cref="Dataset"/> when it started (a share to a new dataset): its rollback deletes the
    /// dataset, and the rollback of a batch on an existing dataset removes only the batch's files (FR-UPL-01, D-049).
    /// False for a batch that started before the column existed.
    /// </summary>
    public bool CreatedDataset { get; set; }

    /// <summary>
    /// The last upload activity of the batch (a chunk received, a file added), UTC: the 24 hours after which an
    /// abandoned batch is rolled back count from it (D-049). Null for a batch that started before the column existed:
    /// its start and the times of its entries count.
    /// </summary>
    public DateTime? LastActivity { get; set; }

    /// <summary>Collection of file entries in this batch.</summary>
    public virtual ICollection<Entry> Entries { get; set; } = null!;
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