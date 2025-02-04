using System.Diagnostics;

// ReSharper disable InvertIf

namespace Eventuous.Diagnostics.Tracing;

public class TracedEventStore : IEventStore {
    public static IEventStore Trace(IEventStore eventStore) => new TracedEventStore(eventStore);

    public TracedEventStore(IEventStore eventStore) => Inner = eventStore;

    IEventStore Inner { get; }

    static readonly KeyValuePair<string, object?>[] DefaultTags = {
        new(TelemetryTags.Db.System, "eventstore")
    };

    public Task<bool> StreamExists(StreamName stream, CancellationToken cancellationToken)
        => Trace(stream, Constants.StreamExists, Inner.StreamExists(stream, cancellationToken));

    public async Task<AppendEventsResult> AppendEvents(
        StreamName                       stream,
        ExpectedStreamVersion            expectedVersion,
        IReadOnlyCollection<StreamEvent> events,
        CancellationToken                cancellationToken
    ) {
        using var activity = StartActivity(stream, Constants.AppendEvents);

        var tracedEvents = events.Select(
            x => x with { Metadata = x.Metadata.AddActivityTags(activity) }
        ).ToArray();

        try {
            return await Inner.AppendEvents(stream, expectedVersion, tracedEvents, cancellationToken).NoContext();
        }
        catch (Exception e) {
            activity?.SetActivityStatus(ActivityStatus.Error(e));
            throw;
        }
    }

    public Task<StreamEvent[]> ReadEvents(
        StreamName         stream,
        StreamReadPosition start,
        int                count,
        CancellationToken  cancellationToken
    )
        => Trace(stream, Constants.ReadEvents, Inner.ReadEvents(stream, start, count, cancellationToken));

    public Task<StreamEvent[]> ReadEventsBackwards(
        StreamName        stream,
        int               count,
        CancellationToken cancellationToken
    )
        => Trace(stream, Constants.ReadEvents, Inner.ReadEventsBackwards(stream, count, cancellationToken));

    public Task<long> ReadStream(
        StreamName          stream,
        StreamReadPosition  start,
        int                 count,
        Action<StreamEvent> callback,
        CancellationToken   cancellationToken
    )
        => Trace(stream, Constants.ReadEvents, Inner.ReadStream(stream, start, count, callback, cancellationToken));

    public Task TruncateStream(
        StreamName             stream,
        StreamTruncatePosition truncatePosition,
        ExpectedStreamVersion  expectedVersion,
        CancellationToken      cancellationToken
    )
        => Trace(
            stream,
            Constants.TruncateStream,
            Inner.TruncateStream(stream, truncatePosition, expectedVersion, cancellationToken)
        );

    public Task DeleteStream(
        StreamName            stream,
        ExpectedStreamVersion expectedVersion,
        CancellationToken     cancellationToken
    )
        => Trace(stream, Constants.DeleteStream, Inner.DeleteStream(stream, expectedVersion, cancellationToken));

    static async Task Trace(StreamName stream, string operation, Task task) {
        using var activity = StartActivity(stream, operation);

        try {
            await task.NoContext();
        }
        catch (Exception e) {
            activity?.SetActivityStatus(ActivityStatus.Error(e));
            throw;
        }
    }

    static async Task<T> Trace<T>(StreamName stream, string operation, Task<T> task) {
        using var activity = StartActivity(stream, operation);

        try {
            return await task.NoContext();
        }
        catch (Exception e) {
            activity?.SetActivityStatus(ActivityStatus.Error(e));
            throw;
        }
    }

    static Activity? StartActivity(
        StreamName stream,
        string     operationName
    ) {
        var activity = EventuousDiagnostics.ActivitySource.CreateActivity(
            operationName,
            ActivityKind.Server,
            parentContext: default,
            DefaultTags,
            idFormat: ActivityIdFormat.W3C
        );

        if (activity is { IsAllDataRequested: true }) {
            activity.SetTag(TelemetryTags.Db.Operation, activity.OperationName);
            activity.SetTag(TelemetryTags.Eventuous.Stream, stream);
        }

        return activity?.Start();
    }
}