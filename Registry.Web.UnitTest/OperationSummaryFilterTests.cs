using Microsoft.OpenApi;
using NUnit.Framework;
using Registry.Web.Filters;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// FR-DEV-12: the title of an operation is the first sentence of its summary; an abbreviation, a decimal
/// number, a version or a URL does not end it.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~OperationSummaryFilterTests"
/// </summary>
[TestFixture]
public sealed class OperationSummaryFilterTests
{
    [TestCase("Lists the datasets.", "Lists the datasets.", "")]
    [TestCase("Lists the datasets", "Lists the datasets", "")]
    [TestCase("  Lists the datasets.  ", "Lists the datasets.", "")]
    [TestCase("Lists the datasets. Paged by 50.", "Lists the datasets.", "Paged by 50.")]
    [TestCase("Is it built? Answers the state.", "Is it built?", "Answers the state.")]
    [TestCase("Stop now! The build ends.", "Stop now!", "The build ends.")]
    [TestCase("First. Second. Third.", "First.", "Second. Third.")]
    [TestCase("Lists the datasets.\nPaged by 50.", "Lists the datasets.", "Paged by 50.")]
    [TestCase("Moves an entry. a lower-case word goes on.", "Moves an entry. a lower-case word goes on.", "")]
    public void Split_NormalSentences(string summary, string title, string rest)
    {
        OperationSummaryFilter.Split(summary).ShouldBe((title, rest));
    }

    [TestCase("Reads a file, e.g. An orthophoto. Then more.", "Reads a file, e.g. An orthophoto.")]
    [TestCase("Reads a file (i.e. The original). Then more.", "Reads a file (i.e. The original).")]
    [TestCase("Builds rasters, clouds, etc. The rest waits. Then more.", "Builds rasters, clouds, etc. The rest waits.")]
    [TestCase("Compares GeoJSON vs. KML output. Then more.", "Compares GeoJSON vs. KML output.")]
    [TestCase("Reads a file, E.G. An orthophoto. Then more.", "Reads a file, E.G. An orthophoto.")]
    [TestCase("Uses the v2.0 API for uploads. Then more.", "Uses the v2.0 API for uploads.")]
    [TestCase("Accepts up to 1.5 GB per chunk. Then more.", "Accepts up to 1.5 GB per chunk.")]
    [TestCase("Follows https://docs.example.com/v1.2/Api.Html for details. Then more.",
        "Follows https://docs.example.com/v1.2/Api.Html for details.")]
    public void Split_DoesNotCutAtAbbreviationsNumbersOrUrls(string summary, string title)
    {
        var (actualTitle, rest) = OperationSummaryFilter.Split(summary);

        actualTitle.ShouldBe(title);
        rest.ShouldBe("Then more.");
    }

    [Test]
    public void Split_AWordEndingLikeAnAbbreviation_StillEndsTheSentence()
    {
        // "canvs." is not "vs.", "fetc." is not "etc."
        OperationSummaryFilter.Split("Draws on a canvs. Then more.").ShouldBe(("Draws on a canvs.", "Then more."));
        OperationSummaryFilter.Split("Reads the fetc. Then more.").ShouldBe(("Reads the fetc.", "Then more."));
    }

    [Test]
    public void Split_TitleIsOneLineWithoutCodeMarks()
    {
        OperationSummaryFilter.Split("Reads the `meta`\n   of an   entry. Then more.")
            .ShouldBe(("Reads the meta of an entry.", "Then more."));
    }

    [Test]
    public void Apply_MovesTheRestBeforeTheDescription()
    {
        var operation = new OpenApiOperation
        {
            Summary = "Lists the datasets, e.g. Public ones. Paged by 50.",
            Description = "Remarks."
        };

        new OperationSummaryFilter().Apply(operation, null!);

        operation.Summary.ShouldBe("Lists the datasets, e.g. Public ones.");
        operation.Description.ShouldBe("Paged by 50.\n\nRemarks.");
    }

    [Test]
    public void Apply_OneSentence_KeepsTheDescription()
    {
        var operation = new OpenApiOperation { Summary = "Uses the v2.0 API.", Description = "Remarks." };

        new OperationSummaryFilter().Apply(operation, null!);

        operation.Summary.ShouldBe("Uses the v2.0 API.");
        operation.Description.ShouldBe("Remarks.");
    }
}
