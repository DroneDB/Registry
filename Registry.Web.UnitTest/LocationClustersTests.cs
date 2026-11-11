using NUnit.Framework;
using Registry.Web.Services.Datasets;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>The representative point of a dataset with data in several places (FR-ORG-09).</summary>
[TestFixture]
public class LocationClustersTests
{
    [Test]
    public void NoPosition_HasNoPoint() => LocationClusters.RepresentativePoint([]).ShouldBeNull();

    [Test]
    public void OnePosition_IsTheOne() => LocationClusters.RepresentativePoint([(12.5, 41.9)]).ShouldBe([12.5, 41.9]);

    [Test]
    public void TheDensestCluster_Wins_AndTheAnswerIsAPositionOfTheData()
    {
        // Three positions near Rome, two in New York, one in Sydney
        var point = LocationClusters.RepresentativePoint(
            [(-74, 40.7), (12.4, 41.8), (151.2, -33.9), (12.5, 41.9), (-73.9, 40.8), (12.6, 41.85)]);

        point.ShouldBe([12.5, 41.9]);
    }

    [Test]
    public void ClustersAcrossNeighbouringCells_AreOneCluster()
    {
        // 9.95 and 10.05 are in cells 9 and 10 of longitude: together they outnumber the two positions in Paris
        var point = LocationClusters.RepresentativePoint([(9.95, 45.5), (10.05, 45.5), (10.0, 45.6), (2.3, 48.8), (2.4, 48.9)]);

        point.ShouldNotBeNull()[0].ShouldBeInRange(9.95, 10.05);
    }

    [Test]
    public void TwoEquallyDenseClusters_GiveTheSouthernOne_WhateverTheOrder()
    {
        LocationClusters.RepresentativePoint([(12.4, 41.8), (-74, 40.7)]).ShouldBe([-74, 40.7]);
        LocationClusters.RepresentativePoint([(-74, 40.7), (12.4, 41.8)]).ShouldBe([-74, 40.7]);
    }

    [TestCase(new[] { 10.0, 20.0, 10.5, 20.5 }, true)]
    [TestCase(new[] { 10.0, 20.0, 11.0, 21.0 }, true)]
    [TestCase(new[] { 10.0, 20.0, 11.1, 20.5 }, false)]
    [TestCase(new[] { -74.0, 40.5, 12.6, 42.0 }, false)]
    public void ABoxOfOnePlace_IsCompact(double[] bbox, bool compact) => LocationClusters.IsCompact(bbox).ShouldBe(compact);
}
