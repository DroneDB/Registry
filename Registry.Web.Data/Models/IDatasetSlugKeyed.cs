namespace Registry.Web.Data.Models;

/// <summary>
/// A row that names its dataset by the organization and dataset slugs instead of pointing at its <c>Datasets</c> row:
/// it follows the dataset when the slugs change (<see cref="SlugKeyedRows"/>).
/// </summary>
public interface IDatasetSlugKeyed
{
    /// <summary>Organization slug of the dataset.</summary>
    string OrgSlug { get; set; }

    /// <summary>Dataset slug.</summary>
    string DsSlug { get; set; }
}
