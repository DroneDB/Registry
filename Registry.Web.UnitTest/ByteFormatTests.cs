using NUnit.Framework;
using Registry.Web.Exceptions;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// FR-XC-26: the 507 text writes a size as <c>formatBytes</c> of the Hub does (<c>libs/format.js</c>: binary units, two
/// decimals at most without trailing zeros, digits grouped). The expected texts are those of the Hub's own spec.
/// </summary>
[TestFixture]
public class ByteFormatTests
{
    [TestCase(0L, "0 B")]
    [TestCase(1L, "1 B")]
    [TestCase(512L, "512 B")]
    [TestCase(1000L, "1,000 B")]
    [TestCase(1023L, "1,023 B")]
    [TestCase(1024L, "1 KB")]
    [TestCase(1536L, "1.5 KB")]
    [TestCase(1048576L, "1 MB")]
    [TestCase(1572864L, "1.5 MB")]
    [TestCase(41_943_040L, "40 MB")]
    [TestCase(1_234_567L, "1.18 MB")]
    [TestCase(10_737_418_240L, "10 GB")]
    [TestCase(1_099_511_627_776L, "1 TB")]
    [TestCase(9_007_199_254_740_992L, "8 PB")]
    public void Format_IsTheHubsFormatBytes(long bytes, string expected) => ByteFormat.Format(bytes).ShouldBe(expected);

    [Test]
    public void TheOwnerOverQuotaText_IsTheOneOfTheSpec()
    {
        QuotaExceededException.OwnerOverQuota(1572864).Message
            .ShouldBe("Storage quota exceeded: the owner of this organization has 1.5 MB left.");
        QuotaExceededException.OwnerOverQuota(-5).Message
            .ShouldBe("Storage quota exceeded: the owner of this organization has 0 B left.");
    }
}
