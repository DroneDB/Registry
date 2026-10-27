#nullable enable
using System;
using System.Linq.Expressions;
using Hangfire.Common;
using NUnit.Framework;
using Registry.Web.Services.Jobs;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// <see cref="DdbJobDescriptor"/> reads a job from the job itself and from the method text its job index row keeps.
/// </summary>
[TestFixture]
public class DdbJobDescriptorTests
{
    private static Job Of(Expression<Action<DdbJobs>> call) => Job.FromExpression(call);

    [Test]
    public void Of_ReadsTheKindAndTheEntryPathOfEachDatasetJob()
    {
        DdbJobDescriptor.Of(Of(j => j.Build("org", Guid.Empty, "a.tif", false, null)))
            .ShouldBe(new DdbJobDescriptor(DdbJobKind.Build, "a.tif"));
        DdbJobDescriptor.Of(Of(j => j.RebuildInBackground("org", Guid.Empty, "m.obj", null)))
            .ShouldBe(new DdbJobDescriptor(DdbJobKind.RebuildInBackground, "m.obj"));
        DdbJobDescriptor.Of(Of(j => j.BuildPending("org", Guid.Empty, null)))
            .ShouldBe(new DdbJobDescriptor(DdbJobKind.BuildPending, null));
        DdbJobDescriptor.Of(Of(j => j.GenerateThumbnail("org", Guid.Empty, "a.tif", 100, "x", null)))
            .ShouldBe(new DdbJobDescriptor(DdbJobKind.GenerateThumbnail, "a.tif"));
    }

    // The text the job index keeps is Hangfire's own (Job.ToString), so it has to read back as the job it names
    [Test]
    public void OfDisplay_ReadsTheMethodTextTheJobIndexKeepsForEachKind()
    {
        DdbJobDescriptor.OfDisplay(Of(j => j.Build("org", Guid.Empty, "a.tif", false, null)).ToString())
            .ShouldBe(new DdbJobDescriptor(DdbJobKind.Build, null));
        DdbJobDescriptor.OfDisplay(Of(j => j.RebuildInBackground("org", Guid.Empty, "m.obj", null)).ToString())
            .ShouldBe(new DdbJobDescriptor(DdbJobKind.RebuildInBackground, null));
        DdbJobDescriptor.OfDisplay(Of(j => j.BuildPending("org", Guid.Empty, null)).ToString())
            .ShouldBe(new DdbJobDescriptor(DdbJobKind.BuildPending, null));
        DdbJobDescriptor.OfDisplay(Job.FromExpression<HangfireUtils>(j => j.BuildWrapper(null!, "a.tif", false, null!)).ToString())
            .ShouldBe(new DdbJobDescriptor(DdbJobKind.Build, null));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("Build")]
    [TestCase("HeavyTaskJobWrapper.Run")]
    [TestCase("DdbJobs.Unknown")]
    public void OfDisplay_IsNullForTextThatNamesNoDatasetJob(string? display) =>
        DdbJobDescriptor.OfDisplay(display).ShouldBeNull();

    [Test]
    public void OfDisplay_TellsTheBackgroundRebuildFromAPlainBuild()
    {
        DdbJobDescriptor.OfDisplay("DdbJobs.RebuildInBackground")!.Value.IsBackgroundRebuild.ShouldBeTrue();
        DdbJobDescriptor.OfDisplay("DdbJobs.Build")!.Value.IsBackgroundRebuild.ShouldBeFalse();
    }
}
