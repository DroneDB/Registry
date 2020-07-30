using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;

namespace Registry.Web.Test
{
    public class OrganizationManagerTest
    {

        private Mock<ILogger<OrganizationsManager>> _loggerMock;
        private Mock<IDatasetsManager> _datasetsManagerMock;
        private IUtils _utils;

        [SetUp]
        public void Setup()
        {
            _loggerMock = new Mock<ILogger<OrganizationsManager>>();
            _datasetsManagerMock = new Mock<IDatasetsManager>();
            _utils = new WebUtils();
        }

        [Test]
        public void GetAllTest()
        {
            

            Assert.Pass();
        }
    }
}