using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;

namespace Registry.Web.Utilities;

/// <summary>
/// The input rules the Hub checks before it submits a form (FR-XC-12, D-014), as <c>GET /sys/features</c> publishes
/// them (<see cref="Describe"/>). Each rule keeps its one definition, which this class only describes for a client:
/// slugs are <see cref="SlugRules"/>, user and role names <see cref="NameRules"/>, e-mail addresses <see cref="Models.Email.EmailAddress.IsValid"/>, cron words
/// <see cref="CronSetting"/>, entry names <see cref="DatasetPathPolicy"/>, system roles <see cref="SystemRoles"/>.
/// It holds the rules that have no other home: the "already exists" message and its case rule (D-040) and the Latin
/// letter rule of organization and dataset names (D-035). A published pattern is ECMAScript (a browser's
/// <c>RegExp</c>); the server check stays the authority.
/// </summary>
public static class InputRules
{
    /// <summary>
    /// <see cref="SlugRules"/> as a pattern: its length, its characters, the first one, no "--". The patterns escape
    /// '-' and '/' in a class, so they compile, with the same answers, without a flag and with the <c>u</c> and
    /// <c>v</c> flags (an HTML <c>pattern</c> attribute uses <c>v</c>).
    /// </summary>
    public static readonly string SlugPattern =
        $@"^(?!.*--)[a-z0-9][a-z0-9_\-]{{{SlugRules.MinLength - 1},{SlugRules.MaxLength - 1}}}$";

    /// <summary>
    /// <see cref="NameRules"/> as a pattern: lowercase letters and digits, <see cref="NameRules.MinLength"/> to
    /// <see cref="NameRules.MaxLength"/> of them. A browser's <c>$</c> does not match before a final line feed, as
    /// .NET's does, so the pattern decides the same as <see cref="NameRules.IsValid"/>.
    /// </summary>
    public static readonly string NamePattern = $"^[a-z0-9]{{{NameRules.MinLength},{NameRules.MaxLength}}}$";

    /// <summary>
    /// A pre-check of an e-mail address: one '@' between characters that are neither whitespace nor '@'. Looser than
    /// <see cref="Models.Email.EmailAddress.IsValid"/> (MimeKit's parser), which decides: a common address it accepts
    /// matches once the client trims it, and some that match (<c>&lt;name@example.com&gt;</c>) are refused there.
    /// </summary>
    public const string EmailPattern = @"^[^\s@]+@[^\s@]+$";

    /// <summary>The e-mail rule as a client shows it.</summary>
    public const string EmailDescription = "one address, such as name@example.com";

    /// <summary>The 400 message of an e-mail address that <see cref="Models.Email.EmailAddress.IsValid"/> refuses.</summary>
    public const string InvalidEmailMessage = "The e-mail address is not valid: enter " + EmailDescription;

    /// <summary>
    /// One entry name as <see cref="DatasetPathPolicy.IsEntryName"/> accepts it: not blank, not "." or "..", no
    /// separator ('/', '\') or NUL, no drive ("C:") at the start. The reserved names are lists of their own
    /// (<see cref="EntryNameReservedNames"/>, <see cref="DatasetPathPolicy.RootReservedNames"/>), not in the pattern;
    /// the aliases libddb also reserves (".ddb.", "DDB~1" and the like) are left to the server.
    /// </summary>
    public const string EntryNamePattern = @"^(?![A-Za-z]:)(?!\.\.?$)(?!\s*$)[^\/\\\u0000]+$";

    /// <summary>The names libddb reserves in every folder (any ASCII letter case).</summary>
    public static readonly string[] EntryNameReservedNames = [IDDB.DatabaseFolderName];

    /// <summary>The entry name rule as a client shows it.</summary>
    public const string EntryNameDescription =
        "a name that is not empty, not '.' or '..', has no '/', '\\' or NUL character, does not start with a " +
        "drive (such as 'C:') and is not a reserved name";

    /// <summary>The "already exists" message of D-040, with <c>{name}</c> where the name goes.</summary>
    public const string NameClashMessageTemplate = "A file or folder named '{name}' already exists in this folder";

    /// <summary>The Registry compares the names in a folder without regard to letter case (D-040).</summary>
    public const bool NamesClashIgnoringCase = true;

    /// <summary>The cron rule as a client shows it.</summary>
    public const string CronDescription =
        "leave it empty for the default schedule; 'disabled', 'off' or 'none' (any case) turns the job off; " +
        "a macro such as '@daily' is a schedule; any other value has 5 or 6 fields separated by spaces";

    /// <summary>
    /// What the normalized name must contain (D-035): an ASCII letter or digit, or a letter the slug generator
    /// transliterates (ø, æ, ß, ł and their capitals, which NFD leaves whole): the characters a slug keeps.
    /// </summary>
    public const string LatinNamePattern = "[A-Za-z0-9ØøÆæßẞŁł]";

    /// <summary>The normalization the slug generator applies first (<see cref="Extenders.ToSlug"/>).</summary>
    public const string LatinNameNormalization = "NFD";

    /// <summary>The Latin letter rule as a client shows it.</summary>
    public const string LatinNameDescription =
        "the name needs a Latin letter (a–z) or a digit (accents are dropped), unless you enter a slug";

    /// <summary>The 400 message of D-035 for a request with a slug field.</summary>
    public const string LatinNameMessage =
        "Use at least one Latin letter (a–z) or digit in the name, or enter a slug";

    /// <summary>The 400 message of D-035 for a request without a slug field (<c>/upload</c>).</summary>
    public const string LatinNameMessageWithoutSlug = "Use at least one Latin letter (a–z) or digit";

    /// <summary>The "already exists" message for <paramref name="name"/>.</summary>
    /// <returns>The message of D-040.</returns>
    public static string NameClashMessage(string name) => NameClashMessageTemplate.Replace("{name}", name);

    /// <summary>
    /// Whether the slug made from <paramref name="name"/> holds a Latin letter or a digit (D-035): what
    /// <see cref="Extenders.ToSlug"/> writes before its fallback ("0") has one, so a letter it transliterates counts
    /// ('Ø' gives "o", 'ß' gives "ss").
    /// </summary>
    /// <returns>False for a null or blank name.</returns>
    public static bool HasLatinLetterOrDigit(string? name) => Extenders.SlugHasLatinLetterOrDigit(name);

    /// <summary>
    /// Refuses a name that holds no Latin letter or digit, when the slug of the request has none either (D-035): the rule
    /// of every request that sets the name of an organization or a dataset (create, edit, meta <c>name</c>,
    /// <c>share/init</c>, import). A blank name has nothing to check (the slug stands for it).
    /// </summary>
    /// <param name="name">The name of the request.</param>
    /// <param name="slug">The slug of the request, or null when it has none.</param>
    /// <param name="field">The member of the request that holds the name (<c>name</c>, <c>datasetName</c>), where the refusal is shown.</param>
    /// <param name="withSlugField">Whether the request has a slug field (the message then tells to enter one).</param>
    /// <exception cref="InvalidFieldException">400 with <c>errors.&lt;field&gt;</c>.</exception>
    public static void EnsureLatinName(string? name, string? slug, string field, bool withSlugField = true)
    {
        if (string.IsNullOrWhiteSpace(name) || HasLatinLetterOrDigit(name) || HasLatinLetterOrDigit(slug))
            return;

        throw new InvalidFieldException(field, withSlugField ? LatinNameMessage : LatinNameMessageWithoutSlug);
    }

    /// <summary>The rules as <c>GET /sys/features</c> publishes them.</summary>
    /// <returns>A new DTO.</returns>
    public static InputRulesDto Describe() => new()
    {
        Slug = new InputRuleDto
        {
            Pattern = SlugPattern,
            MinLength = SlugRules.MinLength,
            MaxLength = SlugRules.MaxLength,
            Description = SlugRules.Description
        },
        Name = new InputRuleDto
        {
            Pattern = NamePattern,
            MinLength = NameRules.MinLength,
            MaxLength = NameRules.MaxLength,
            Description = NameRules.Description
        },
        Email = new InputRuleDto
        {
            Pattern = EmailPattern,
            Description = EmailDescription,
            Message = InvalidEmailMessage
        },
        EntryName = new EntryNameRuleDto
        {
            Pattern = EntryNamePattern,
            ReservedNames = [.. EntryNameReservedNames],
            RootReservedNames = [.. DatasetPathPolicy.RootReservedNames],
            Description = EntryNameDescription
        },
        NameClash = new NameClashRuleDto
        {
            Message = NameClashMessageTemplate,
            CaseInsensitive = NamesClashIgnoringCase
        },
        Cron = new CronRuleDto
        {
            DisableWords = [.. CronSetting.DisableWords],
            MinFields = CronSetting.MinFields,
            MaxFields = CronSetting.MaxFields,
            Macros = [.. CronSetting.Macros],
            Description = CronDescription
        },
        LatinName = new LatinNameRuleDto
        {
            Pattern = LatinNamePattern,
            Normalization = LatinNameNormalization,
            Description = LatinNameDescription,
            Message = LatinNameMessage,
            MessageWithoutSlug = LatinNameMessageWithoutSlug
        },
        DisplayName = new InputRuleDto
        {
            MinLength = SlugRules.NameMinLength,
            MaxLength = SlugRules.NameMaxLength,
            Description = SlugRules.NameDescription,
            Message = SlugRules.NameTooLongMessage
        },
        DescriptionText = new InputRuleDto
        {
            MaxLength = SlugRules.DescriptionMaxLength,
            Description = SlugRules.DescriptionLimitDescription,
            Message = SlugRules.DescriptionTooLongMessage
        }
    };
}
