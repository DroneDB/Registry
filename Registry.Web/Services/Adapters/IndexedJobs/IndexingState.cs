using System;
using System.Collections.Generic;
using Hangfire.States;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Initial, non-runnable state of an indexed job. Hangfire persists arbitrary non-final states but
/// does not dispatch them to workers, so no worker can run the job (and lose a terminal state or
/// error against a missing index row) before publication moves it to Enqueued/Scheduled. The state
/// data carries the publication record that recovery needs to finish an interrupted publication.
/// </summary>
public sealed class IndexingState(IndexedJobPublicationRecord record) : IState
{
    public const string StateName = "Indexing";

    public IndexedJobPublicationRecord Record { get; } = record ?? throw new ArgumentNullException(nameof(record));

    public string Name => StateName;
    public string Reason => "Publishing indexed job metadata";
    public bool IsFinal => false;
    public bool IgnoreJobLoadException => false;
    public Dictionary<string, string> SerializeData() => Record.ToStateData();
}
