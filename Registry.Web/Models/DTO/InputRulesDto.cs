namespace Registry.Web.Models.DTO;

/// <summary>
/// The input rules of the Registry that a client checks before it submits a form (FR-XC-12, D-014), published by
/// <c>GET /sys/features</c>. A pattern is ECMAScript (what a browser's <c>RegExp</c> runs). The server check stays the
/// authority: a client that reads an older server, without a rule, uses its own default.
/// </summary>
public class InputRulesDto
{
    /// <summary>Organization and dataset slugs.</summary>
    public InputRuleDto? Slug { get; set; }

    /// <summary>User and role names (D-007, D-015): lowercase letters and digits.</summary>
    public InputRuleDto? Name { get; set; }

    /// <summary>E-mail addresses: the pattern is a pre-check, looser than the server's parser.</summary>
    public InputRuleDto? Email { get; set; }

    /// <summary>The name of one file or folder in a dataset (one path segment).</summary>
    public EntryNameRuleDto? EntryName { get; set; }

    /// <summary>The refusal of a name that a folder already holds.</summary>
    public NameClashRuleDto? NameClash { get; set; }

    /// <summary>The words that switch off a scheduled job in a cron setting.</summary>
    public CronRuleDto? Cron { get; set; }

    /// <summary>An organization or dataset name needs a Latin letter or a digit, or a slug the user enters (D-035).</summary>
    public LatinNameRuleDto? LatinName { get; set; }
}

/// <summary>
/// One input rule: the pattern a value must match, its length limits and the text that tells the rule.
/// </summary>
public class InputRuleDto
{
    /// <summary>ECMAScript pattern that a valid value matches.</summary>
    public string? Pattern { get; set; }

    /// <summary>The fewest characters; null when the pattern alone decides.</summary>
    public int? MinLength { get; set; }

    /// <summary>The most characters; null when the pattern alone decides.</summary>
    public int? MaxLength { get; set; }

    /// <summary>The rule as a client shows it at the field.</summary>
    public string? Description { get; set; }

    /// <summary>The message of the server's refusal; null when the refusal names the field it applies to.</summary>
    public string? Message { get; set; }
}

/// <summary>
/// The name of one file or folder in a dataset: one path segment that the dataset path rule accepts
/// (<c>DatasetPathPolicy</c>, libddb's <c>DDBNormalizePath</c>), and not a reserved name.
/// </summary>
public class EntryNameRuleDto
{
    /// <summary>ECMAScript pattern of a name: not blank, not '.' or '..', no '/', '\' or NUL, no drive ('C:') at the start.</summary>
    public string? Pattern { get; set; }

    /// <summary>Names refused in every folder, compared without regard to the case of ASCII letters.</summary>
    public string[]? ReservedNames { get; set; }

    /// <summary>Names refused at the dataset root, compared as written.</summary>
    public string[]? RootReservedNames { get; set; }

    /// <summary>The rule as a client shows it at the field.</summary>
    public string? Description { get; set; }
}

/// <summary>
/// The refusal of a name that exists in the folder (D-040): one message for rename, move, create folder, uploads and
/// tool outputs, and the case rule that names are compared by.
/// </summary>
public class NameClashRuleDto
{
    /// <summary>The message, with <c>{name}</c> where the name goes.</summary>
    public string? Message { get; set; }

    /// <summary>Two names in a folder clash when they differ only by letter case.</summary>
    public bool CaseInsensitive { get; set; }
}

/// <summary>
/// How a cron setting is read: empty takes the job's default, a disable word removes the job.
/// </summary>
public class CronRuleDto
{
    /// <summary>The words that switch the job off, in lowercase; a setting matches them in any case.</summary>
    public string[]? DisableWords { get; set; }

    /// <summary>The fewest fields of a cron expression (a value that is empty or a disable word has none).</summary>
    public int? MinFields { get; set; }

    /// <summary>The most fields of a cron expression.</summary>
    public int? MaxFields { get; set; }

    /// <summary>The macros that are a whole cron value (such as "@daily"), in lowercase; a setting matches them in any case.</summary>
    public string[]? Macros { get; set; }

    /// <summary>The rule as a client shows it at the field.</summary>
    public string? Description { get; set; }
}

/// <summary>
/// The rule of D-035: an organization or dataset name is refused when neither the slug made from it nor the slug the
/// request gives holds a Latin letter (a–z) or a digit. The slug made from a name keeps the Latin letters and digits
/// of its <see cref="Normalization"/> form (accents dropped).
/// </summary>
public class LatinNameRuleDto
{
    /// <summary>ECMAScript pattern that the normalized name must contain (a search, not a whole-value match).</summary>
    public string? Pattern { get; set; }

    /// <summary>The Unicode normalization form the name is put in before the search (<c>NFD</c>).</summary>
    public string? Normalization { get; set; }

    /// <summary>The rule as a client shows it at the field.</summary>
    public string? Description { get; set; }

    /// <summary>The message of the refusal when the request has a slug field.</summary>
    public string? Message { get; set; }

    /// <summary>The message of the refusal when the request has no slug field (<c>/upload</c>).</summary>
    public string? MessageWithoutSlug { get; set; }
}
