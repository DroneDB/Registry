using System;
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
        public const string StorageCleanupJobId = "cleanup-storage";

        public const string TileCacheSeed = "tile";
        public const string ThumbnailCacheSeed = "thumb";
    }
}
