
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FluentAssertions;
using NUnit.Framework;
using Registry.Common;


namespace Registry.Adapters.Test;

[TestFixture]
public class UtilsTest
{

    private static readonly string TestString = new string(Enumerable.Range(0, 1024 * 1024 * 5 + 731).Select(item => 'A').ToArray());

    [Test]
    [Explicit]
    public void RandomString_Ok()
    {
        var lst = new List<string>();
        for (var n = 0; n < 10000; n++)
        {
            lst.Add(CommonUtils.RandomString(16));
        }

        lst.Count.Should().Be(lst.Distinct().Count());
    }

}