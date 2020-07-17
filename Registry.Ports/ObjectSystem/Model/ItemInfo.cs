using System;
using System.Collections.Generic;

namespace Registry.Ports.ObjectSystem.Model
{
    public class ItemInfo
    {

        public string Key { get; set; }
        public ulong Size { get; set; }

        public bool IsDir { get; set; }

        public DateTime? LastModified { get; set; }
    }
}