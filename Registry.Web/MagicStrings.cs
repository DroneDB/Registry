﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Registry.Web
{
    
    // This class is the definition of a bad idea
    public static class MagicStrings
    {
        public const string PublicOrganizationSlug = "public";
        public const string DefaultDatasetSlug = "default";
        public const string AnonymousUserName = "anonymous";
        public const string MaxStorageKey = "maxStorageMB";

        public const string TileCacheSeed = "tile";
        public const string ThumbnailCacheSeed = "thumb";
        
        public const string IdentityConnectionName = "IdentityConnection";
        public const string RegistryConnectionName = "RegistryConnection";
        public const string HangfireConnectionName = "HangfireConnection";
        
        public const string HangFireUrl = "/hangfire";
        public const string SwaggerUrl = "/swagger";
        public const string QuickHealthUrl = "/quickhealth";
        public const string HealthUrl = "/health";
        public const string VersionUrl = "/version";
    }
}
