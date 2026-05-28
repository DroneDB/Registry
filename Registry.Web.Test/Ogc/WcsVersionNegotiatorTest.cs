using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using Registry.Web.Exceptions;
using Registry.Web.Utilities.Ogc;
using Shouldly;

namespace Registry.Web.Test.Ogc;

/// <summary>
/// Tests for <see cref="WcsVersionNegotiator"/>. WCS 1.0/1.1/2.0 use different negotiation
/// rules (single VERSION vs ACCEPTVERSIONS list); the negotiator unifies them.
/// </summary>
[TestFixture]
public class WcsVersionNegotiatorTest
{
    [Test]
    public void NoVersion_DefaultsToHighestSupported()
    {
        WcsVersionNegotiator.Negotiate(null, null).ShouldBe("2.0.1");
        WcsVersionNegotiator.Negotiate("", "").ShouldBe("2.0.1");
    }

    [TestCase("1.0.0", "1.0.0")]
    [TestCase("1.0", "1.0.0")]
    [TestCase("1.1.0", "1.1.1")]
    [TestCase("1.1.1", "1.1.1")]
    [TestCase("1.1", "1.1.1")]
    [TestCase("2.0", "2.0.1")]
    [TestCase("2.0.1", "2.0.1")]
    public void Version_ExactOrMajorMinorMatch(string requested, string expected)
    {
        WcsVersionNegotiator.Negotiate(requested, null).ShouldBe(expected);
    }

    [Test]
    public void Version_HigherThanSupported_FallsBackToHighest()
    {
        // OGC 03-065r6 §6.2.1: server picks highest supported <= requested.
        WcsVersionNegotiator.Negotiate("3.0.0", null).ShouldBe("2.0.1");
    }

    [Test]
    public void AcceptVersions_PicksFirstSupported()
    {
        WcsVersionNegotiator.Negotiate(null, "2.0.1,1.1.1,1.0.0").ShouldBe("2.0.1");
        WcsVersionNegotiator.Negotiate(null, "1.0.0,2.0.1").ShouldBe("1.0.0");
        WcsVersionNegotiator.Negotiate(null, "1.1.1").ShouldBe("1.1.1");
    }

    [Test]
    public void AcceptVersions_NoneSupported_Throws()
    {
        var ex = Should.Throw<OgcException>(() =>
            WcsVersionNegotiator.Negotiate(null, "0.9.0,3.0.0"));
        ex.Code.ShouldBe("VersionNegotiationFailed");
    }

    [Test]
    public void AcceptVersions_WinsOverVersion()
    {
        // ACCEPTVERSIONS is the OWS 2.0 idiom and should take precedence when present.
        WcsVersionNegotiator.Negotiate("2.0.1", "1.0.0").ShouldBe("1.0.0");
    }
}
