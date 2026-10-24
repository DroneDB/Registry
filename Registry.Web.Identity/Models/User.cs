using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Identity;

namespace Registry.Web.Identity.Models;

/// <summary>
/// This class represents an user
/// </summary>
public class User : IdentityUser
{
    private Dictionary<string, object> _metadata = new();

    /*
     *  When updating the entity and changing items in the dictionary,
     *  the EF change tracking does not pick up on the fact that the dictionary was updated,
     *  so you will need to explicitly call the Update method on the DbSet<> to set the entity
     *  to modified in the change tracker.
     */
    /// <summary>
    /// Free-form metadata (e.g. from the login provider). Never null: assigning null stores an empty
    /// dictionary, and a row that reads back as null (legacy NULL or the JSON literal <c>null</c>)
    /// is seen as empty, so the column (NOT NULL, <c>{}</c> when empty) never gets a NULL written.
    /// </summary>
    [AllowNull]
    public Dictionary<string, object> Metadata
    {
        get => _metadata ??= new();
        set => _metadata = value ?? new();
    }
}
