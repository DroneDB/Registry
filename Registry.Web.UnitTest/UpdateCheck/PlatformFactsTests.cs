#nullable enable
using System;
using System.IO;
using NUnit.Framework;
using Moq;
using Registry.Ports;
using Registry.Web.Services.UpdateCheck;
using Shouldly;

namespace Registry.Web.Test.UpdateCheck;

/// <summary>
/// What the machine says about itself: the container check reads the two files Linux leaves through <see cref="IFileSystem"/>.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~PlatformFactsTests"
/// </summary>
[TestFixture]
public class PlatformFactsTests
{
    private Mock<IFileSystem> _fs = null!;

    [SetUp]
    public void SetUp()
    {
        Assume.That(OperatingSystem.IsLinux(), "The container files are those of Linux");
        _fs = new Mock<IFileSystem>();
    }

    private bool InContainer() => new PlatformFacts(_fs.Object).Capture().InContainer;

    [Test]
    public void TheDockerMarker_MeansAContainer()
    {
        _fs.Setup(f => f.Exists("/.dockerenv")).Returns(true);

        InContainer().ShouldBeTrue();
    }

    [TestCase("12:cpu:/kubepods/burstable/pod1", true)]
    [TestCase("0::/system.slice/containerd.service", true)]
    [TestCase("0::/user.slice/user-1000.slice", false)]
    public void TheControlGroup_TellsAContainerFromAHost(string cgroup, bool expected)
    {
        _fs.Setup(f => f.Exists("/proc/1/cgroup")).Returns(true);
        _fs.Setup(f => f.ReadAllText("/proc/1/cgroup")).Returns(cgroup);

        InContainer().ShouldBe(expected);
    }

    [TestCase(typeof(IOException))]
    [TestCase(typeof(UnauthorizedAccessException))]
    public void AControlGroupThatCannotBeRead_IsNotAContainer(Type failure)
    {
        _fs.Setup(f => f.Exists("/proc/1/cgroup")).Returns(true);
        _fs.Setup(f => f.ReadAllText("/proc/1/cgroup")).Throws((Exception)Activator.CreateInstance(failure)!);

        InContainer().ShouldBeFalse();
    }
}
