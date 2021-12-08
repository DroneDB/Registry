using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutoMapper.Configuration;
using Microsoft.CodeAnalysis.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Common;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters
{
    public class BatchTokenGenerator : IBatchTokenGenerator
    {
        private readonly int _tokenLength;

        public const int MinTokenLength = 8;

        public BatchTokenGenerator(IOptions<AppSettings> settings, ILogger<BatchTokenGenerator> logger)
        {
            _tokenLength = settings.Value.BatchTokenLength;

            if (_tokenLength < MinTokenLength)
            {
                logger.LogWarning("Invalid BatchTokenLength ({TokenLength}), capped to {MinTokenLength}", _tokenLength, MinTokenLength);
                _tokenLength = MinTokenLength;
            }
        }

        public string GenerateToken()
        {
            return CommonUtils.RandomString(_tokenLength);
        }
    }
}
