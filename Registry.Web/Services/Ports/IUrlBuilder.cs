using Registry.Web.Data.Models;

namespace Registry.Web.Services.Ports;

/// <summary>
/// The absolute URLs Registry gives its resources (RQ-12, from <c>IUtils</c>): the public host (the external URL
/// override, else the request's), the dataset URLs.
/// </summary>
public interface IUrlBuilder
{
    /// <summary>
    /// The public base URL, e.g. <c>https://hub.example</c>; with <paramref name="useDdbScheme"/> the <c>ddb://</c>
    /// (<c>ddb+unsafe://</c> over HTTP) form the DroneDB client opens.
    /// </summary>
    string GetLocalHost(bool useDdbScheme = false);

    /// <summary>
    /// The API URL of a dataset (<c>{host}/orgs/{org}/ds/{ds}</c>), or with <paramref name="useDdbScheme"/> its
    /// DroneDB client URL (<c>ddb://{host}/{org}/{ds}</c>).
    /// </summary>
    string GenerateDatasetUrl(Dataset dataset, bool useDdbScheme = false);
}
