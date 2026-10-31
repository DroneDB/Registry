using System.Xml;
using NUnit.Framework;
using Registry.Web.Utilities.Ogc;
using Shouldly;

namespace Registry.Web.Test.Ogc;

/// <summary>FES 2.0 filters DroneDB evaluates in SQL, and those left to the Registry.</summary>
[TestFixture]
public class FesSqlTranslatorTests
{
    private static readonly string[] Fields = ["name", "pop", "code", "it's"];

    private static FesSqlFilter? Translate(string predicate)
    {
        var doc = new XmlDocument();
        doc.LoadXml("<fes:Filter xmlns:fes=\"http://www.opengis.net/fes/2.0\" xmlns:gml=\"http://www.opengis.net/gml/3.2\" " +
                    "xmlns:ddb=\"http://www.dronedb.app/wfs\">" + predicate + "</fes:Filter>");
        return FesSqlTranslator.Translate(doc.DocumentElement!, Fields);
    }

    private static string? Where(string predicate) => Translate(predicate)?.Where;

    private static string Cmp(string op, string a, string b, string attrs = "") =>
        $"<fes:{op}{attrs}>{a}{b}</fes:{op}>";

    private static string Ref(string name) => $"<fes:ValueReference>{name}</fes:ValueReference>";
    private static string Lit(string value) => $"<fes:Literal>{value}</fes:Literal>";

    [TestCase("PropertyIsEqualTo", "=")]
    [TestCase("PropertyIsNotEqualTo", "<>")]
    [TestCase("PropertyIsLessThan", "<")]
    [TestCase("PropertyIsGreaterThan", ">")]
    [TestCase("PropertyIsLessThanOrEqualTo", "<=")]
    [TestCase("PropertyIsGreaterThanOrEqualTo", ">=")]
    public void Comparisons(string op, string sql)
    {
        Where(Cmp(op, Ref("ddb:pop"), Lit("100"))).ShouldBe($"\"pop\" {sql} '100'");
    }

    [TestCase("PropertyIsLessThan", ">")]
    [TestCase("PropertyIsGreaterThanOrEqualTo", "<=")]
    [TestCase("PropertyIsEqualTo", "=")]
    public void LiteralFirstMirrorsTheOperator(string op, string sql)
    {
        Where(Cmp(op, Lit("100"), Ref("pop"))).ShouldBe($"\"pop\" {sql} '100'");
    }

    [Test]
    public void QuotesLiteralsAndIdentifiers()
    {
        Where(Cmp("PropertyIsEqualTo", Ref("name"), Lit("x' OR '1'='1"))).ShouldBe("\"name\" = 'x'' OR ''1''=''1'");
        Where(Cmp("PropertyIsEqualTo", Ref("it's"), Lit("a"))).ShouldBe("\"it's\" = 'a'");
    }

    [Test]
    public void LogicalOperators()
    {
        var a = Cmp("PropertyIsEqualTo", Ref("name"), Lit("A"));
        var b = Cmp("PropertyIsGreaterThan", Ref("pop"), Lit("5"));
        Where($"<fes:Or>{a}{b}</fes:Or>").ShouldBe("(\"name\" = 'A' OR \"pop\" > '5')");
        Where($"<fes:Not>{a}</fes:Not>").ShouldBe("(NOT \"name\" = 'A')");
        Where($"<fes:And>{a}<fes:Or>{a}<fes:Not>{b}</fes:Not></fes:Or></fes:And>")
            .ShouldBe("\"name\" = 'A' AND (\"name\" = 'A' OR (NOT \"pop\" > '5'))");
    }

    [Test]
    public void BetweenNullAndNil()
    {
        Where($"<fes:PropertyIsBetween>{Ref("pop")}<fes:LowerBoundary>{Lit("1")}</fes:LowerBoundary>" +
              $"<fes:UpperBoundary>{Lit("9")}</fes:UpperBoundary></fes:PropertyIsBetween>")
            .ShouldBe("\"pop\" BETWEEN '1' AND '9'");
        Where($"<fes:PropertyIsNull>{Ref("code")}</fes:PropertyIsNull>").ShouldBe("(\"code\" IS NULL OR \"code\" = '')");
        Where($"<fes:PropertyIsNil>{Ref("code")}</fes:PropertyIsNil>").ShouldBe("(\"code\" IS NULL OR \"code\" = '')");
    }

    [Test]
    public void LikeIsGlobWhenCaseMatters()
    {
        Where(Cmp("PropertyIsLike", Ref("name"), Lit("Ro*a?[x]\\*"), " wildCard=\"*\" singleChar=\"?\" escapeChar=\"\\\""))
            .ShouldBe("\"name\" GLOB 'Ro*a?[[]x][*]'");
        Where(Cmp("PropertyIsLike", Ref("name"), Lit("50%_off#"), " wildCard=\"#\" singleChar=\".\" escapeChar=\"!\""))
            .ShouldBe("\"name\" GLOB '50%_off*'");
    }

    [Test]
    public void LikeIgnoringCaseIsLikeWithEscapes()
    {
        Where(Cmp("PropertyIsLike", Ref("name"), Lit("50%_*"), " wildCard=\"*\" singleChar=\"?\" escapeChar=\"\\\" matchCase=\"false\""))
            .ShouldBe("\"name\" LIKE '50\\%\\_%' ESCAPE '\\'");
        Where(Cmp("PropertyIsLike", Ref("name"), Lit("città*"), " matchCase=\"false\"")).ShouldBeNull("non-ASCII");
    }

    [Test]
    public void CaseInsensitiveEqualityOnly()
    {
        Where(Cmp("PropertyIsEqualTo", Ref("name"), Lit("rome"), " matchCase=\"false\""))
            .ShouldBe("\"name\" = 'rome' COLLATE NOCASE");
        Where(Cmp("PropertyIsLessThan", Ref("name"), Lit("rome"), " matchCase=\"false\"")).ShouldBeNull();
        Where(Cmp("PropertyIsEqualTo", Ref("name"), Lit("città"), " matchCase=\"false\"")).ShouldBeNull();
    }

    [Test]
    public void BboxAloneOrInTheTopLevelAnd()
    {
        const string bbox = "<fes:BBOX><fes:ValueReference>geom</fes:ValueReference>" +
                            "<gml:Envelope srsName=\"urn:ogc:def:crs:EPSG::4326\"><gml:lowerCorner>45 9</gml:lowerCorner>" +
                            "<gml:upperCorner>46 10</gml:upperCorner></gml:Envelope></fes:BBOX>";

        var alone = Translate(bbox)!;
        alone.Where.ShouldBeNull();
        alone.Bbox.ShouldBe([9, 45, 10, 46]);

        var and = Translate($"<fes:And>{bbox}{Cmp("PropertyIsEqualTo", Ref("name"), Lit("A"))}</fes:And>")!;
        and.Where.ShouldBe("\"name\" = 'A'");
        and.Bbox.ShouldBe([9, 45, 10, 46]);

        Translate($"<fes:Or>{bbox}{Cmp("PropertyIsEqualTo", Ref("name"), Lit("A"))}</fes:Or>").ShouldBeNull();
        Translate($"<fes:And>{bbox}{bbox}</fes:And>").ShouldBeNull();
    }

    [Test]
    public void LeftToTheRegistry()
    {
        Where(Cmp("PropertyIsEqualTo", Ref("ddbSeq"), Lit("1"))).ShouldBeNull("synthetic property");
        Where(Cmp("PropertyIsEqualTo", Ref("missing"), Lit("1"))).ShouldBeNull("not a field: SQLite would read it as a string");
        Where(Cmp("PropertyIsEqualTo", Ref("pop"), Lit("1"), " matchAction=\"All\"")).ShouldBeNull();
        Where(Cmp("PropertyIsEqualTo", Lit("1"), Lit("1"))).ShouldBeNull();
        Where(Cmp("PropertyIsEqualTo", Ref("pop"), "<fes:Literal><gml:Point/></fes:Literal>")).ShouldBeNull();
        Where("<fes:ResourceId rid=\"a.1\"/>").ShouldBeNull();
        Where($"<fes:And>{Cmp("PropertyIsEqualTo", Ref("pop"), Lit("1"))}<fes:ResourceId rid=\"a.1\"/></fes:And>")
            .ShouldBeNull();
        Where($"<fes:PropertyIsEqualTo><fes:Function name=\"x\"/>{Lit("1")}</fes:PropertyIsEqualTo>").ShouldBeNull();
    }

    [TestCase("ddb:foo/ddb:bar[1]", "bar")]
    [TestCase("pop", "pop")]
    [TestCase(" ddb:pop ", "pop")]
    public void PropertyNames(string reference, string name) =>
        FesSqlTranslator.PropertyName(reference).ShouldBe(name);
}
