using Registry.Ports;
using Registry.Web.Services.Datasets;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.HeavyTasks;

/// <summary>
/// What a task needs, from its own scope, to create (or take again) the dataset it imports its results into: the
/// system creation of the dataset, the access of the user the task runs for (read without a request), and the index of
/// the dataset.
/// </summary>
/// <param name="Creation">The system creation of the dataset and its slug checks.</param>
/// <param name="RequesterAccess">The access of the user the task runs for, by the rules of the API.</param>
/// <param name="DdbManager">The index of the dataset.</param>
public sealed record ResultsDatasetServices(
    DatasetCreation Creation,
    IUserDatasetAccess RequesterAccess,
    IDdbManager DdbManager);
