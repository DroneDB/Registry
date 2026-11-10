using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Registry.Web.Services.Jobs;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// FR-PRC-42: the build jobs write their percent and phase only when <see cref="DdbJobs"/> is given a
/// <see cref="BuildJobProgress"/>, and its constructor parameter is optional (a null one builds without progress). So a
/// registration that goes missing would turn the progress off with no error: the composition of the real web host pins it.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~DdbJobsCompositionTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class DdbJobsCompositionTests
{
    /// <summary>The progress a <see cref="DdbJobs"/> holds (its only holder is the constructor parameter).</summary>
    internal static BuildJobProgress? ProgressOf(DdbJobs jobs)
    {
        var field = typeof(DdbJobs).GetField("_progress", BindingFlags.Instance | BindingFlags.NonPublic);
        field.ShouldNotBeNull("DdbJobs keeps its BuildJobProgress in '_progress': update this test if it was renamed");
        return (BuildJobProgress?)field.GetValue(jobs);
    }

    [Test]
    public void TheWebHost_InjectsTheBuildProgress_IntoTheBuildJobs()
    {
        using var factory = new RegistryWebFactory();
        using var scope = factory.Services.CreateScope();

        var jobs = scope.ServiceProvider.GetRequiredService<DdbJobs>();

        ProgressOf(jobs).ShouldNotBeNull();
        ProgressOf(jobs).ShouldBeSameAs(factory.Services.GetRequiredService<BuildJobProgress>(),
            "the jobs share the one registered BuildJobProgress");
    }
}
