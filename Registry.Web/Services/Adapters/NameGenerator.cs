using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Common;
using Registry.Ports;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;

namespace Registry.Web.Services.Adapters
{
    public class NameGenerator : INameGenerator
    {

        private readonly int _nameLength;
        public const int MinNameLength = 8;

        public NameGenerator(IOptions<AppSettings> settings, ILogger<NameGenerator> logger)
        {
            _nameLength = settings.Value.RandomDatasetNameLength;

            if (_nameLength < MinNameLength)
            {
                logger.LogWarning("Invalid RandomDatasetNameLength ({NameLength}), capped to {MinNameLength}", _nameLength, MinNameLength);
                _nameLength = MinNameLength;
            }

        }

        public string GenerateName()
        {
            return CommonUtils.RandomString(_nameLength).ToLowerInvariant();
        }
    }
}
