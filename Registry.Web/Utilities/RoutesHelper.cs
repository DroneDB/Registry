using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Registry.Web.Utilities
{
    public static class RoutesHelper
    {
        public const string OrganizationsRadix = "orgs";
        public const string DatasetRadix = "ds";
        public const string OrganizationSlug = "{orgSlug:regex(" + SlugRegex + ")}";
        public const string DatasetSlug = "{dsSlug:regex(" + SlugRegex + ")}";
        public const string SlugRegex = "^[[a-z0-9]][[a-z0-9_-]]{{1,128}}$";
        public const string ShareRadix = "share";
        public const string UsersRadix = "users";
        public const string ObjectsRadix = "obj";
        public const string SystemRadix = "sys";
        public const string PushRadix = "push";
        public const string MetaRadix = "meta";
        public const string StacRadix = "stac";

    }
}
