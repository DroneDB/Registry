using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;

namespace Registry.Web.Test
{
    public class OrganizationManagerTest
    {

        private Mock<ILogger<OrganizationsManager>> _loggerMock = new Mock<ILogger<OrganizationsManager>>();
        private Mock<IDatasetsManager> _datasetsManagerMock = new Mock<IDatasetsManager>();
        private IUtils _utils = new WebUtils();

        [SetUp]
        public void Setup()
        {
        }

        [Test]
        public void GetAllTest()
        {
            

            Assert.Pass();
        }
    }
}