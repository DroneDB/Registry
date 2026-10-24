namespace Registry.Test.Common;

/// <summary>
/// URLs of the DroneDB/test_data fixtures, pinned to one commit (XI-09).
/// </summary>
/// <remarks>
/// <para>Every test downloads test_data through <see cref="Url"/>, never from a branch
/// (<c>raw/master</c>), so a push to test_data cannot change what a Registry test sees.
/// <c>scripts/quality-metrics.sh</c> counts unpinned test_data URLs (baseline 0).</para>
/// <para><see cref="Commit"/> is the commit DroneDB pins in its single source,
/// <c>DroneDB/tests/test-data.sha</c>. Move both together: bump that file first, then copy the
/// SHA here, so libddb and Registry test against the same data.</para>
/// </remarks>
public static class TestData
{
    /// <summary>The DroneDB/test_data commit the tests use (same as DroneDB's tests/test-data.sha).</summary>
    public const string Commit = "aa1db3cc04ee97cb0ccbeedc5c089e300af653c4";

    /// <summary>Download base of the pinned commit, with a trailing slash.</summary>
    public const string BaseUrl = "https://github.com/DroneDB/test_data/raw/" + Commit + "/";

    /// <summary>
    /// The download URL of <paramref name="path"/> (relative to the test_data root, '/' separated)
    /// at <see cref="Commit"/>.
    /// </summary>
    public static string Url(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return BaseUrl + path.TrimStart('/');
    }
}
