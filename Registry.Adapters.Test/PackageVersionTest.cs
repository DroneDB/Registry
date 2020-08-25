using System;
using System.Collections.Generic;
using System.Text;
using FluentAssertions;
using NUnit.Framework;
using Registry.Common;

namespace Registry.Adapters.Test
{
    [TestFixture]
    public class PackageVersionTest
    {
        [Test]
        public void PackageVersion_Equals_True()
        {
            var v1 = new PackageVersion(1, 2, 3);
            var v2 = new PackageVersion(1,2,3);

            v1.Should().BeEquivalentTo(v2);
            (v1 == v2).Should().BeTrue();
            (v1 >= v2).Should().BeTrue();
            (v1 <= v2).Should().BeTrue();

        }

        [Test]
        public void PackageVersion_Null_False()
        {
            var v1 = new PackageVersion(1, 2, 3);
            PackageVersion v2 = null;

            (v1 == v2).Should().BeFalse();
        }

        [Test]
        public void PackageVersion_Greater_True_1()
        {
            var v1 = new PackageVersion(2, 2, 3);
            var v2 = new PackageVersion(1, 2, 3);

            (v1 > v2).Should().BeTrue();
            (v1 >= v2).Should().BeTrue();
            (v1 < v2).Should().BeFalse();
            (v1 <= v2).Should().BeFalse();

        }

        [Test]
        public void PackageVersion_Greater_True_2()
        {
            var v1 = new PackageVersion(1, 3, 3);
            var v2 = new PackageVersion(1, 2, 3);

            (v1 > v2).Should().BeTrue();
            (v1 >= v2).Should().BeTrue();
            (v1 < v2).Should().BeFalse();
            (v1 <= v2).Should().BeFalse();

        }

        [Test]
        public void PackageVersion_Greater_True_3()
        {
            var v1 = new PackageVersion(1, 2, 4);
            var v2 = new PackageVersion(1, 2, 3);

            (v1 > v2).Should().BeTrue();
            (v1 >= v2).Should().BeTrue();
            (v1 < v2).Should().BeFalse();
            (v1 <= v2).Should().BeFalse();

        }

        [Test]
        public void PackageVersion_Smaller_True_1()
        {
            var v1 = new PackageVersion(1, 2, 3);
            var v2 = new PackageVersion(2, 2, 3);

            (v1 > v2).Should().BeFalse();
            (v1 >= v2).Should().BeFalse();
            (v1 < v2).Should().BeTrue();
            (v1 <= v2).Should().BeTrue();

        }

        [Test]
        public void PackageVersion_Smaller_True_2()
        {
            var v1 = new PackageVersion(1, 2, 3);
            var v2 = new PackageVersion(1, 3, 3);

            (v1 > v2).Should().BeFalse();
            (v1 >= v2).Should().BeFalse();
            (v1 < v2).Should().BeTrue();
            (v1 <= v2).Should().BeTrue();

        }

        [Test]
        public void PackageVersion_Smaller_True_3()
        {
            var v1 = new PackageVersion(1, 2, 3);
            var v2 = new PackageVersion(1, 2, 4);

            (v1 > v2).Should().BeFalse();
            (v1 >= v2).Should().BeFalse();
            (v1 < v2).Should().BeTrue();
            (v1 <= v2).Should().BeTrue();

        }
    }
}
