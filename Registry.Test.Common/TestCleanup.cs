namespace Registry.Test.Common;

/// <summary>
/// What runs before a test deletes a file or folder it made (<see cref="TestArea"/>, <see cref="TempFile"/>).
/// </summary>
public static class TestCleanup
{
    /// <summary>
    /// Called with the path before it is deleted. A test assembly that loads libddb sets it to close what libddb keeps
    /// open there (pooled index connections, cached rasters): Windows deletes no file a process holds open.
    /// </summary>
    public static Action<string>? BeforeDelete { get; set; }
}
