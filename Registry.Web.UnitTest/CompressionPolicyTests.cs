#nullable enable
using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>Responses that carry tokens are not compressed (BREACH).</summary>
[TestFixture]
public class CompressionPolicyTests
{
    [TestCase("/users/authenticate", true)]
    [TestCase("/users/authenticate/refresh", true)]
    [TestCase("/users/changepwd", true)]
    [TestCase("/users/alice/changepwd", true)]
    [TestCase("/share/init", true)]
    [TestCase("/share/info/0123456789abcdef", true)]
    [TestCase("/orgs/acme/ds/survey/push/init", true)]
    [TestCase("/orgs/acme/ds/survey/batches", true)]
    [TestCase("/sys/cleanupbatches", true)]
    [TestCase("/users/storage", false)]
    [TestCase("/users", false)]
    [TestCase("/orgs/acme/ds/survey/list", false)]
    [TestCase("/orgs/acme/ds", false)]
    [TestCase("/share/upload/0123456789abcdef", false)]
    [TestCase("/main.12345678.js", false)]
    public void CarriesSecrets_MatchesTheTokenResponsesOnly(string path, bool expected) =>
        CompressionPolicy.CarriesSecrets(new PathString(path)).ShouldBe(expected);
}
