using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;

namespace Registry.Web.Test
{
    public class ObjectManagerTest
    {
        private Mock<ILogger<IObjectsManager>> _loggerMock = new Mock<ILogger<IObjectsManager>>();
        private Mock<IObjectsManager> _objectsManagerMock = new Mock<IObjectsManager>();
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
