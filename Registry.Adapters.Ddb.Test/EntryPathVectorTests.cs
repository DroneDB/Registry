using System;
using System.Linq;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Adapters.Testing;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Shouldly;
using static Registry.Adapters.Ddb.Test.LibddbNative;

namespace Registry.Adapters.Ddb.Test;

/// <summary>
/// On the real libddb: every golden path vector (DroneDB
/// <c>tests/contract/vectors/entry-paths.json</c>, vendored in <c>Registry.Adapters.Testing</c>)
/// through the port (<see cref="NativeDdbWrapper.NormalizePath"/>, <c>DDBNormalizePath</c>). The
/// <c>cString: false</c> vectors (a NUL byte, which the C API cannot receive) are answered by the
/// adapter's managed check before the call, with the same code libddb's C++ rule gives. Registry's
/// <c>DatasetPathPolicy</c> is this call plus its two documented additions; the unit tests run the
/// same vectors through <c>FakeDdbWrapper</c>.
/// Run: dotnet test Registry.Adapters.Ddb.Test --filter "FullyQualifiedName~EntryPathVectorTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Native)]
public class EntryPathVectorTests : TestBase
{
    private static readonly NativeDdbWrapper Wrapper = new();

    [OneTimeSetUp]
    public void Setup()
    {
        UseDdbFolder();
        var (gate, message) = CheckAbi();
        if (gate != Gate.Applies)
            Assert.Fail(message);

        new NativeDdbRuntime().RegisterProcess();
    }

    private static PathVector[] Vectors() => [.. PathVectors.All];

    [TestCaseSource(nameof(Vectors))]
    public void NormalizePath_AnswersEveryGoldenVector(PathVector vector)
    {
        Wrapper.NormalizePath(vector.Input, vector.Reserved).ShouldBe(vector.Expected);
    }

    [Test]
    public void TheVectorsFileHasEveryRule()
    {
        PathVectors.All.Count.ShouldBe(54);
        PathVectors.All.Select(v => v.Expected.Rejected).Where(r => r != null).Distinct().Order()
            .ShouldBe([EntryPathRejection.Absolute, EntryPathRejection.Empty, EntryPathRejection.Nul,
                EntryPathRejection.Reserved, EntryPathRejection.Traversal]);
    }

    // A lone surrogate would reach libddb as U+FFFD (the UTF-8 marshaller replaces it): the adapter
    // answers "encoding", the code libddb gives to invalid UTF-8. Built at run time: an attribute
    // argument is stored as UTF-8, which cannot hold a lone surrogate.
    [TestCase(0xD800)]
    [TestCase(0xDC00)]
    public void NormalizePath_LoneSurrogate_IsEncoding(int surrogate)
    {
        Wrapper.NormalizePath("a" + (char)surrogate + "b").Rejected.ShouldBe(EntryPathRejection.Encoding);
    }

    [TestCase("a/b")]
    [TestCase("..")]
    [TestCase(".")]
    public void NormalizePath_InvalidReservedName_IsAnArgumentError(string name)
    {
        Should.Throw<ArgumentException>(() => Wrapper.NormalizePath("a", [name]));
    }

    [Test]
    public void NormalizePath_SurrogatePair_IsKept()
    {
        Wrapper.NormalizePath("emoji/🚁.jpg").ShouldBe(NormalizedEntryPath.Accepted("emoji/🚁.jpg"));
    }
}
