using DDB.Bindings.Model;
using Registry.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Registry.Web.Models.DTO
{
    public class DeltaDto
    {

        public AddActionDto[] Adds { get; set; }

        public RemoveActionDto[] Removes { get; set; }

    }

    public class AddActionDto
    {
        public string Path { get; set; }

        public EntryType Type { get; set; }

        public override string ToString() =>
            $"ADD -> [{(Type == EntryType.Directory ? 'D' : 'F')}] {Path}";


    }

    public class RemoveActionDto
    {
        public string Path { get; set; }

        public EntryType Type { get; set; }

        public override string ToString() => $"DEL -> [{(Type == EntryType.Directory ? 'D' : 'F')}] {Path}";


    }
}
