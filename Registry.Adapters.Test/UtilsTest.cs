
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FluentAssertions;
using NUnit.Framework;
using Registry.Common;


namespace Registry.Adapters.Test
{
    [TestFixture]
    public class UtilsTest
    {

        private static readonly string TestString = new string(Enumerable.Range(0, 1024 * 1024 * 5 + 731).Select(item => 'A').ToArray());

        [Test]
        public void CalculateMultipartEtag_ShortFile_Ok()
        {

            //const string expectedEtag1 = "e6065c4aa2ab1603008fc18410f579d4e6065c4aa2ab1603008fc18410f579d4e6065c4aa2ab1603008fc18410f579d4e6065c4aa2ab1603008fc18410f579d4e6065c4aa2ab1603008fc18410f579d4-5";

            using var testArea = new TestArea(nameof(CalculateMultipartEtag_ShortFile_Ok));

            var testFile = Path.Combine(testArea.TestFolder, "temp.txt");
            File.WriteAllText(testFile, TestString);

            var etag = AdaptersUtils.CalculateMultipartEtag(File.ReadAllBytes(testFile), 2);

            using var stream = File.OpenRead(testFile);
            var res = AdaptersUtils.CalculateMultipartEtag(stream, 2);

            res.Should().Be(etag);
        }

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
}