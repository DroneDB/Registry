using System;

namespace Registry.Web.Models.DTO;

public class UserDetailDto
{
    public string? Id { get; set; }
    public string? UserName { get; set; }
    public string? Email { get; set; }
    public string[]? Roles { get; set; }
    public string[]? Organizations { get; set; }
    public long? StorageQuota { get; set; }
    public long StorageUsed { get; set; }
    public int OrganizationCount { get; set; }
    public int DatasetCount { get; set; }
    /// <summary>When the user was created (UTC); null for a user created before the Registry stored it.</summary>
    public DateTime? CreatedDate { get; set; }
}
