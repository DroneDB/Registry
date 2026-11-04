
using System;
using System.Collections.Generic;
using System.Linq;
using Shouldly;
using NUnit.Framework;
using Registry.Common;


namespace Registry.Adapters.Test;

[TestFixture]
public class UtilsTest
{

    [Test]
    [Explicit]
    public void RandomString_Ok()
    {
        var lst = new List<string>();
        for (var n = 0; n < 10000; n++)
        {
            lst.Add(CommonUtils.RandomString(16));
        }

        lst.Count.ShouldBe(lst.Distinct().Count());
    }

}