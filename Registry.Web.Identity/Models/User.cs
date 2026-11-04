using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Identity;

namespace Registry.Web.Identity.Models;

/// <summary>
/// This class represents an user
/// </summary>
public class User : IdentityUser
{
    private Dictionary<string, object> _metadata = new();
    private Dictionary<string, object> _preferences = new();

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

    /// <summary>
    /// The user's Hub preferences, one property per section ("map", "units", ...): JSON the Hub owns. The Registry
    /// reads only the "notifications" section (which build e-mails the user wants) and checks its shape when it is
    /// written; the other sections are free-form. Only the user writes it (<c>PATCH /users/preferences</c>); a login provider and the admin
    /// metadata endpoint never do, which is why it is a column of its own and not a key of <see cref="Metadata"/>.
    /// Never null, like <see cref="Metadata"/>.
    /// </summary>
    [AllowNull]
    public Dictionary<string, object> Preferences
    {
        get => _preferences ??= new();
        set => _preferences = value ?? new();
    }

    /// <summary>
    /// The name of a user of the identity store. Identity's user validator refuses a user without a name, so a user
    /// that was read from the store or created in it always has one (a method, so that EF does not map it).
    /// </summary>
    /// <exception cref="InvalidOperationException">The user was never stored and has no name.</exception>
    public string RequiredUserName() =>
        UserName ?? throw new InvalidOperationException($"The user '{Id}' has no user name");
}
