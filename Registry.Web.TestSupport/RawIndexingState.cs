using System.Collections.Generic;
using Hangfire.States;
using Registry.Web.Services.Adapters;

namespace Registry.Web.Test;

/// <summary>State with raw (possibly malformed) data under the <c>Indexing</c> name.</summary>
public sealed class RawIndexingState(Dictionary<string, string>? data) : IState
{
    public string Name => IndexingState.StateName;
    public string Reason => "raw";
    public bool IsFinal => false;
    public bool IgnoreJobLoadException => false;
    public Dictionary<string, string> SerializeData() => data!;
}
