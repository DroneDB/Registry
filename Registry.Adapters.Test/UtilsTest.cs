
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FluentAssertions;
using NUnit.Framework;
using Registry.Common;
using Registry.Ports.ObjectSystem.Model;

namespace Registry.Adapters.Test
{
    [TestFixture]
    public class UtilsTest
    {

        private static readonly string TestString =  new string(Enumerable.Range(0, 1024 * 1024 * 5 + 731).Select(item => 'A').ToArray());

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
        public void ToS3CopyConditions_Simple_Ok()
        {
            var cc = new CopyConditions();
            cc.SetModified(DateTime.Now);
            cc.SetByteRange(0, 100);

            var newcc = cc.ToS3CopyConditions();

            cc.GetConditions().Should().BeEquivalentTo(newcc.GetConditions());
        }


    }
}