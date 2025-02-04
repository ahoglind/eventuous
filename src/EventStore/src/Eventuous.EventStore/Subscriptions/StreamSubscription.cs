using Eventuous.EventStore.Subscriptions.Diagnostics;
using Eventuous.Subscriptions.Checkpoints;
using Eventuous.Subscriptions.Context;
using Eventuous.Subscriptions.Diagnostics;
using Eventuous.Subscriptions.Filters;

namespace Eventuous.EventStore.Subscriptions;

/// <summary>
/// Catch-up subscription for EventStoreDB, for a specific stream
/// </summary>
[PublicAPI]
public class StreamSubscription
    : EventStoreCatchUpSubscriptionBase<StreamSubscriptionOptions>, IMeasuredSubscription {
    /// <summary>
    /// Creates EventStoreDB catch-up subscription service for a given stream
    /// </summary>
    /// <param name="eventStoreClient">EventStoreDB gRPC client instance</param>
    /// <param name="streamName">Name of the stream to receive events from</param>
    /// <param name="subscriptionId">Subscription ID</param>
    /// <param name="checkpointStore">Checkpoint store instance</param>
    /// <param name="consumerPipe"></param>
    /// <param name="eventSerializer">Event serializer instance</param>
    /// <param name="metaSerializer"></param>
    /// <param name="throwOnError"></param>
    public StreamSubscription(
        EventStoreClient     eventStoreClient,
        StreamName           streamName,
        string               subscriptionId,
        ICheckpointStore     checkpointStore,
        ConsumePipe          consumerPipe,
        IEventSerializer?    eventSerializer = null,
        IMetadataSerializer? metaSerializer  = null,
        bool                 throwOnError    = false
    ) : this(
        eventStoreClient,
        new StreamSubscriptionOptions {
            StreamName         = streamName,
            SubscriptionId     = subscriptionId,
            ThrowOnError       = throwOnError,
            EventSerializer    = eventSerializer,
            MetadataSerializer = metaSerializer
        },
        checkpointStore,
        consumerPipe
    ) { }

    /// <summary>
    /// Creates EventStoreDB catch-up subscription service for a given stream
    /// </summary>
    /// <param name="client"></param>
    /// <param name="checkpointStore">Checkpoint store instance</param>
    /// <param name="options">Subscription options</param>
    /// <param name="consumePipe"></param>
    public StreamSubscription(
        EventStoreClient          client,
        StreamSubscriptionOptions options,
        ICheckpointStore          checkpointStore,
        ConsumePipe               consumePipe
    ) : base(client, options, checkpointStore, consumePipe)
        => Ensure.NotEmptyString(options.StreamName);

    protected override async ValueTask Subscribe(CancellationToken cancellationToken) {
        var (_, position) = await GetCheckpoint(cancellationToken).NoContext();

        var subTask = position == null
            ? EventStoreClient.SubscribeToStreamAsync(
                Options.StreamName,
                HandleEvent,
                Options.ResolveLinkTos,
                HandleDrop,
                Options.ConfigureOperation,
                Options.Credentials,
                cancellationToken
            )
            : EventStoreClient.SubscribeToStreamAsync(
                Options.StreamName,
                StreamPosition.FromInt64((long)position),
                HandleEvent,
                Options.ResolveLinkTos,
                HandleDrop,
                Options.ConfigureOperation,
                Options.Credentials,
                cancellationToken
            );

        Subscription = await subTask.NoContext();

        async Task HandleEvent(
            global::EventStore.Client.StreamSubscription _,
            ResolvedEvent                                re,
            CancellationToken                            ct
        ) {
            if (Options.IgnoreSystemEvents && re.Event.EventType[0] == '$') return;

            // Despite ResolvedEvent.Event being not marked as nullable, it returns null for deleted events
            // ReSharper disable once ConditionIsAlwaysTrueOrFalse
            if (re.Event is null) return;

            await HandleInternal(CreateContext(re, ct)).NoContext();
        }

        void HandleDrop(
            global::EventStore.Client.StreamSubscription _,
            SubscriptionDroppedReason                    reason,
            Exception?                                   ex
        )
            => Dropped(EsdbMappings.AsDropReason(reason), ex);
    }

    IMessageConsumeContext CreateContext(ResolvedEvent re, CancellationToken cancellationToken) {
        var evt = DeserializeData(
            re.Event.ContentType,
            re.Event.EventType,
            re.Event.Data,
            re.Event.EventStreamId,
            re.Event.EventNumber
        );

        return new MessageConsumeContext(
                re.Event.EventId.ToString(),
                re.Event.EventType,
                re.Event.ContentType,
                re.Event.EventStreamId,
                re.OriginalEventNumber,
                re.Event.Created,
                evt,
                DeserializeMeta(re.Event.Metadata, re.OriginalStreamId, re.Event.EventNumber),
                SubscriptionId,
                cancellationToken
            )
            .WithItem(ContextKeys.GlobalPosition, re.Event.Position.CommitPosition)
            .WithItem(ContextKeys.StreamPosition, re.OriginalEventNumber.ToUInt64());
    }

    public GetSubscriptionGap GetMeasure()
        => new StreamSubscriptionMeasure(
            Options.SubscriptionId,
            Options.StreamName,
            EventStoreClient,
            () => LastProcessed
        ).GetSubscriptionGap;
}