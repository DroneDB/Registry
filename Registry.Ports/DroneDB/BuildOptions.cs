namespace Registry.Ports.DroneDB;

/// <summary>
/// What limits one build (<c>DDBBuildEx</c>). A limit only lowers the host's own budget
/// (<c>DDB_BUILD_THREADS</c>), it never raises it.
/// </summary>
/// <param name="Threads">The most threads the build may use (1 to 4096), or null for no cap.</param>
public sealed record BuildOptions(int? Threads = null)
{
    /// <summary>The options of a build that uses at most <paramref name="threads"/> threads.</summary>
    public static BuildOptions Capped(int threads) => new(threads);
}
