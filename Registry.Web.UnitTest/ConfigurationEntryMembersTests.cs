using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Configuration;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The description of a JSON field of the admin configuration view names the members its entries accept as
/// the settings class binds them, so the text cannot list a member Registry does not read.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~ConfigurationEntryMembersTests"
/// </summary>
[TestFixture]
public class ConfigurationEntryMembersTests
{
    private enum Mode { On, Off }

    private sealed class Entry
    {
        public Mode State { get; set; }
        public string? Label { get; set; }
        public List<string> Names { get; set; } = [];
        public bool Flag { get; set; }
        public int Count { get; set; }
        public double? Ratio { get; set; }
        public NodeOdxNodeConfig Node { get; set; } = new();
        public string Computed => Label ?? "";
    }

    [Test]
    public void EntryMembers_NamesTheSettableProperties_InDeclarationOrder_WithTheirKind()
    {
        ConfigurationFields.EntryMembers<Entry>().ShouldBe(
            "State (On|Off), Label, Names (array), Flag (bool), Count (number), Ratio (number), Node (object)");
    }

    [Test]
    public void TheToolGatingDescription_NamesEveryMemberOfAToolEntry_AndNoOther()
    {
        var description = ToolGatingField().Description.ShouldNotBeNull();

        const string start = "Each entry accepts: ";
        var from = description.IndexOf(start, StringComparison.Ordinal);
        from.ShouldBeGreaterThan(-1, description);
        from += start.Length;
        var listed = description[from..description.IndexOf(". ", from, StringComparison.Ordinal)]
            .Split(", ")
            .Select(m => m.Split(' ')[0])
            .ToArray();

        var members = typeof(HeavyToolConfig).GetProperties().Where(p => p.CanWrite).Select(p => p.Name);
        listed.ShouldBe(members, ignoreOrder: true, customMessage: description);
    }

    private static ConfigurationFieldDataDto ToolGatingField()
    {
        using var factory = new RegistryWebFactory();
        using var scope = factory.Services.CreateScope();
        var sections = scope.ServiceProvider.GetServices<IConfigurationSectionBuilder>()
            .SelectMany(b => b.Build())
            .Select(p => p.Section);
        return sections.Single(s => s.Title == "Tool Gating").Fields.ShouldNotBeNull().Single(f => f.Key == "Tools");
    }
}
