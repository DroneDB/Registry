using System;
using System.IO;
using Moq;
using Registry.Ports;

namespace Registry.Web.Test.Support;

/// <summary>Set-ups of <see cref="IDdbManager"/> mocks.</summary>
public static class DdbManagerMocks
{
    /// <summary>Puts the dataset folders of <paramref name="ddbManager"/> under <paramref name="datasetsPath"/>.</summary>
    public static Mock<IDdbManager> WithFolders(this Mock<IDdbManager> ddbManager, string datasetsPath) =>
        ddbManager.WithFolders(() => datasetsPath);

    /// <summary>Puts the dataset folders under the datasets path of the moment (a test that changes its settings).</summary>
    public static Mock<IDdbManager> WithFolders(this Mock<IDdbManager> ddbManager, Func<string?> datasetsPath)
    {
        ArgumentNullException.ThrowIfNull(ddbManager);
        ddbManager.Setup(m => m.GetDatasetFolder(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns((string org, Guid internalRef) =>
                Path.Combine(datasetsPath() ?? string.Empty, org, internalRef.ToString()));
        return ddbManager;
    }
}
