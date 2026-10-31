using System;
using System.ComponentModel.DataAnnotations;
using Registry.Common;
using Registry.Web.Services.Managers;

namespace Registry.Web.Models.DTO;

public class OrganizationPermissionsDto
{
    public bool CanRead { get; set; }
    public bool CanWrite { get; set; }
    public bool CanDelete { get; set; }
    public bool CanManageMembers { get; set; }

    public static OrganizationPermissionsDto FromPermissions(OrganizationPermissions permissions)
    {
        return new OrganizationPermissionsDto
        {
            CanRead = permissions.HasAccess(AccessType.Read),
            CanWrite = permissions.HasAccess(AccessType.Write),
            CanDelete = permissions.HasAccess(AccessType.Delete),
            CanManageMembers = permissions.CanManageMembers()
        };
    }

    public static readonly OrganizationPermissionsDto ReadOnly = new()
    {
        CanRead = true,
        CanWrite = false,
        CanDelete = false,
        CanManageMembers = false
    };
}

/// <summary>
/// An organization as the API answers it (output only: what a client may send is
/// <see cref="OrganizationRequestDto"/>).
/// </summary>
public class OrganizationDto
{
    public string? Slug { get; set; }

    public string? Name { get; set; }
    public string? Description { get; set; }
    public DateTime CreationDate { get; set; }
    public string? Owner { get; set; }

    public bool IsPublic { get; set; }

    /// <summary>
    /// How many datasets the organization has. Null for callers that are not its owner, an administrator or a
    /// member (and in the public list).
    /// </summary>
    public int? DatasetCount { get; set; }

    /// <summary>
    /// When the newest change reached the index of one of its datasets (its creation date when it has none that can be
    /// read). Null for callers that are not its owner, an administrator or a member (and in the public list).
    /// </summary>
    public DateTime? LastModified { get; set; }

    /// <summary>
    /// The permissions of the current user in this organization.
    /// </summary>
    public OrganizationPermissionsDto? Permissions { get; set; }
}

/// <summary>
/// What a client sends to create (<c>POST /orgs</c>) or edit (<c>PUT /orgs/{org}</c>) an organization, as a form.
/// The members of the answer that a client does not set (<c>CreationDate</c>, <c>Permissions</c>) are not here, so a
/// request cannot bind them. <see cref="Owner"/> is a user id: only an administrator may name an owner other than
/// the caller (<c>OrganizationsManager</c> refuses anybody else with 403). Members the client sends that are not
/// listed are ignored.
/// </summary>
public class OrganizationRequestDto
{
    // [ApiController] answers 400 before the action runs when a [Required] member is missing, so it is never null there
    [Required]
    public string Slug { get; init; } = null!;

    [Required]
    public string Name { get; init; } = null!;

    public string? Description { get; init; }

    public string? Owner { get; init; }

    public bool IsPublic { get; init; }
}
