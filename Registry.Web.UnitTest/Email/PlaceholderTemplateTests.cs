#nullable enable
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Registry.Web.Services.Email;
using Shouldly;

namespace Registry.Web.Test.Email;

/// <summary>
/// The placeholder text of the editable e-mails: what is refused (and how the message names it), and the paragraph
/// structure both parts are made from.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~PlaceholderTemplateTests"
/// </summary>
[TestFixture]
public class PlaceholderTemplateTests
{
    private static readonly IReadOnlyList<EmailPlaceholder> Allowed =
    [
        new("name", "A name", InSubject: true),
        new("url", "A link", InSubject: false),
        new("list", "A block", InSubject: false, IsBlock: true)
    ];

    private static PlaceholderTemplate Body(string text) => PlaceholderTemplate.Parse(text, Allowed, TemplateField.Body);

    private static string? Value(string name) => name switch
    {
        "name" => "Jane",
        "url" => "https://example.org/x",
        "list" => "- a\n- b\n",
        _ => null
    };

    [Test]
    public void Parse_SplitsTextAndPlaceholders()
    {
        Body("Hi {{name}}, {{ name }}!").Parts.ShouldBe(
        [
            new TemplateText("Hi "), new TemplateValue("name"), new TemplateText(", "), new TemplateValue("name"),
            new TemplateText("!")
        ]);
    }

    [Test]
    public void Parse_TextWithoutPlaceholders_IsOneSegment_AndSingleBracesAreText()
    {
        Body("a { b } }} c").Parts.ShouldBe([new TemplateText("a { b } }} c")]);
    }

    [TestCase("Hi {{nope}}", TemplateField.Body, "Unknown placeholder {{nope}} in the body; the placeholders are {{name}}, {{url}}, {{list}}")]
    [TestCase("Hi {{nope}}", TemplateField.Subject, "Unknown placeholder {{nope}} in the subject; the placeholders are {{name}}")]
    [TestCase("Hi {{Name}}", TemplateField.Body, "Unknown placeholder {{Name}}")]
    [TestCase("Hi {{name", TemplateField.Body, "Unclosed placeholder \"{{name\" in the body")]
    [TestCase("{{ }}", TemplateField.Body, "Invalid placeholder {{ }} in the body")]
    [TestCase("{{a b}}", TemplateField.Body, "Invalid placeholder {{a b}}")]
    [TestCase("{{<script>}}", TemplateField.Body, "Invalid placeholder {{<script>}}")]
    [TestCase("{{list}}", TemplateField.Subject, "The placeholder {{list}} can only be used in the body, not in the subject")]
    [TestCase("{{url}}", TemplateField.Subject, "The placeholder {{url}} can only be used in the body")]
    public void Parse_Refuses_NamingTheBadToken(string text, TemplateField field, string expected)
    {
        Should.Throw<PlaceholderTemplateException>(() => PlaceholderTemplate.Parse(text, Allowed, field))
            .Message.ShouldContain(expected);
    }

    [Test]
    public void Parse_AnUnclosedPlaceholder_QuotesOnlyTheStartOfALongText()
    {
        var message = Should.Throw<PlaceholderTemplateException>(() => Body("{{" + new string('x', 500))).Message;

        message.Length.ShouldBeLessThan(120);
    }

    [Test]
    public void RenderLine_InsertsValues_AndMakesOneLine()
    {
        PlaceholderTemplate.Parse("Hi {{name}}\r\n  again ", Allowed, TemplateField.Subject).RenderLine(Value)
            .ShouldBe("Hi Jane again");
    }

    [Test]
    public void Blocks_BlankLinesSeparateParagraphs_LineBreaksStayInside()
    {
        var blocks = Body("  Hello {{name}},  \r\nsecond line\n\n\n   \nnext paragraph\n").Blocks(_ => true);

        blocks.Count.ShouldBe(2);
        var first = blocks[0].ShouldBeOfType<TemplateParagraph>();
        first.Lines.Count.ShouldBe(2);
        first.Lines[0].ShouldBe([new TemplateText("Hello "), new TemplateValue("name"), new TemplateText(",")]);
        first.Lines[1].ShouldBe([new TemplateText("second line")]);
        blocks[1].ShouldBeOfType<TemplateParagraph>().Lines.ShouldHaveSingleItem().ShouldBe([new TemplateText("next paragraph")]);
    }

    [Test]
    public void Blocks_ABlockPlaceholder_EndsTheParagraphAroundIt()
    {
        var blocks = Body("Before {{list}} after").Blocks(_ => true);

        blocks.Count.ShouldBe(3);
        blocks[1].ShouldBe(new TemplateBlockValue("list"));
        ((TemplateParagraph)blocks[2]).Lines.Single().ShouldBe([new TemplateText("after")]);
    }

    [Test]
    public void Blocks_AParagraphUsingAPlaceholderWithoutValue_IsLeftOut_WithItsWords()
    {
        var blocks = Body("Hi {{name}}\n\nOpen {{url}} now\n\n{{list}}").Blocks(name => name != "url");

        blocks.Count.ShouldBe(2);
        blocks.OfType<TemplateParagraph>().SelectMany(p => p.Names).ShouldBe(["name"]);
    }

    [Test]
    public void Alone_IsTheNameOfAPlaceholderStandingAloneInItsParagraph()
    {
        var blocks = Body("{{url}}\n\nGo: {{url}}").Blocks(_ => true);

        ((TemplateParagraph)blocks[0]).Alone.ShouldBe("url");
        ((TemplateParagraph)blocks[1]).Alone.ShouldBeNull();
    }

    [Test]
    public void RenderText_IsTheSameStructure_AsText()
    {
        Body("Hi {{name}},\nwelcome\n\n{{list}}\n\nSee {{url}}").RenderText(Value)
            .ShouldBe("Hi Jane,\nwelcome\n\n- a\n- b\n\nSee https://example.org/x");
    }

    [Test]
    public void RenderText_LeavesOutTheParagraphsWithoutValue()
    {
        Body("Hi {{name}}\n\nSee {{url}}").RenderText(name => name == "url" ? null : Value(name)).ShouldBe("Hi Jane");
    }
}
