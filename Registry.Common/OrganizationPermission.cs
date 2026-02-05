namespace Registry.Common;

/// <summary>
/// Permission levels for organization members.
/// Higher values include all permissions of lower values.
/// </summary>
public enum OrganizationPermission
{
    /// <summary>
    /// Can only read/view datasets and organization info
    /// </summary>
    ReadOnly = 0,

    /// <summary>
    /// Can read, write (upload/modify) and build datasets
    /// </summary>
    ReadWrite = 1,

    /// <summary>
    /// Can read, write, build and delete datasets
    /// </summary>
    ReadWriteDelete = 2,

    /// <summary>
    /// Full access including member management
    /// </summary>
    Admin = 3
}
