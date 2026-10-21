using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Registry.Web.Test.Support;

namespace Registry.Web.Test;

[TestFixture]
public class WebUtilsTest : TestBase
{

    private Mock<IAuthManager> _authManagerMock;
    private MutableOptions<AppSettings> _settingsOptions;
    private Mock<IHttpContextAccessor> _httpContextAccessorMock;
    private Mock<IDdbManager> _ddbManagerMock;

    [SetUp]
    public void Setup()
    {
        _settingsOptions = TestSettings.Mutable();
        _authManagerMock = new Mock<IAuthManager>();
        _httpContextAccessorMock = new Mock<IHttpContextAccessor>();
        _ddbManagerMock = new Mock<IDdbManager>();
    }


    [Test]
    public async Task GetFreeOrganizationSlug_SimpleCase()
    {
        await using var context = GetTest1Context();
        _settingsOptions.Value = _settings;
        _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));

        var webUtils = new WebUtils(_authManagerMock.Object, context, _settingsOptions,
            _httpContextAccessorMock.Object, TestCaches.Summaries(_ddbManagerMock.Object));

        const string organizationName = "uav4geo";
        const string expectedOrganizationSlug = "uav4geo";

        var slug = webUtils.GetFreeOrganizationSlug(organizationName);

        slug.ShouldBe(expectedOrganizationSlug);

    }


    [Test]
    public async Task GetFreeOrganizationSlug_OverlappingCase()
    {
        await using var context = GetTest1Context();
        _settingsOptions.Value = _settings;
        _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));

        var webUtils = new WebUtils(_authManagerMock.Object, context, _settingsOptions,
            _httpContextAccessorMock.Object, TestCaches.Summaries(_ddbManagerMock.Object));

        const string organizationName = "public";
        const string expectedOrganizationSlug = "public-1";

        var slug = webUtils.GetFreeOrganizationSlug(organizationName);

        slug.ShouldBe(expectedOrganizationSlug);

    }

    [Test]
    public async Task GetFreeOrganizationSlug_DoubleOverlappingCase()
    {
        await using var context = GetTest1Context();
        _settingsOptions.Value = _settings;
        _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));

        const string organizationName = "public";
        const string expectedOrganizationSlug = "public-2";

        Seed.Organization(context, MagicStrings.PublicOrganizationSlug + "-1", organizationName + "-1",
            isPublic: true, description: organizationName + "-1 organization");
        await context.SaveChangesAsync();

        var webUtils = new WebUtils(_authManagerMock.Object, context, _settingsOptions,
            _httpContextAccessorMock.Object, TestCaches.Summaries(_ddbManagerMock.Object));

        var slug = webUtils.GetFreeOrganizationSlug(organizationName);

        slug.ShouldBe(expectedOrganizationSlug);

    }

    [Test]
    public async Task GetFreeOrganizationSlug_TripleOverlappingCase()
    {
        await using var context = GetTest1Context();
        _settingsOptions.Value = _settings;
        _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));

        const string organizationName = "public";
        const string expectedOrganizationSlug = "public-3";

        Seed.Organization(context, MagicStrings.PublicOrganizationSlug + "-1", organizationName + "-1",
            isPublic: true, description: organizationName + "-1 organization");

        Seed.Organization(context, MagicStrings.PublicOrganizationSlug + "-2", organizationName + "-2",
            isPublic: true, description: organizationName + "-2 organization");

        await context.SaveChangesAsync();

        var webUtils = new WebUtils(_authManagerMock.Object, context, _settingsOptions,
            _httpContextAccessorMock.Object, TestCaches.Summaries(_ddbManagerMock.Object));


        var slug = webUtils.GetFreeOrganizationSlug(organizationName);

        slug.ShouldBe(expectedOrganizationSlug);

    }

    [Test]
    public void ToSlug_EmptyString_Exception()
    {
        var str = string.Empty;

        Should.Throw<ArgumentException>(() => str.ToSlug());
    }

    [Test]
    public void ToSlug_SimpleString_ValidSlug()
    {
        const string str = "òàùè";

        var slug = str.ToSlug();

        slug.ShouldBe("oaue");

        slug.IsValidSlug().ShouldBeTrue();
    }

    [Test]
    public void ToSlug_ComplexString_ValidSlug()
    {
        const string str = ":;:ç°§ç§é*{1._↓-&%$/&%$)=(/\n\ta";

        var slug = str.ToSlug();

        slug.ShouldBe("0-c-c-e-1-_-a");

        slug.IsValidSlug().ShouldBeTrue();

    }

    [Test]
    public void ToSlug_ComplexString2_ValidSlug()
    {
        const string str = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789éèàù°§ç§:;,.-_$&%()=+*{}[]|@#~?/*§!\"'<>";

        var slug = str.ToSlug();

        slug.ShouldBe(
                "abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyz0123456789eeau-c-_-");

        slug.IsValidSlug().ShouldBeTrue();

    }

    [TestCase("")]
    [TestCase("-abc")]
    [TestCase("_abc")]
    [TestCase("Abc")]
    [TestCase("a b")]
    [TestCase("a/b")]
    [TestCase("jane--eric")]
    [TestCase("wawa---w-16th-ave-hialeah")]
    public void IsValidSlug_Invalid(string slug)
    {
        slug.IsValidSlug().ShouldBeFalse();
    }

    #region Test Data

    private readonly AppSettings _settings = TestSettings.Default;


    #endregion

    #region TestContexts

    private static RegistryContext GetTest1Context()
    {
        // Insert seed data into the database using one instance of the context
        return TestDb.CreateContext<RegistryContext>(context =>
        {
            Seed.PublicDefaultDataset(context);

            context.SaveChanges();
        });
    }

    #endregion
}