using System.Collections.Concurrent;
using System.Net.ServerSentEvents;
using System.Threading.Channels;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<OrderEventBuffer>();
builder.Services.AddSingleton<OrderBroadcaster>();
builder.Services.AddHostedService<OrderProducer>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// The plain stream: hand an IAsyncEnumerable<T> to Results.ServerSentEvents and
// ASP.NET Core writes the "event:" / "data:" frames and keeps the response open.
app.MapGet("orders/realtime", (
    OrderBroadcaster broadcaster,
    CancellationToken cancellationToken) =>
{
    var subscription = broadcaster.Subscribe();

    async IAsyncEnumerable<OrderPlacement> StreamOrders()
    {
        using (subscription)
        {
            await foreach (var item in subscription.Reader.ReadAllAsync(cancellationToken))
            {
                yield return item.Data!;
            }
        }
    }

    return Results.ServerSentEvents(StreamOrders(), eventType: "orders");
});

// Same stream, but every frame carries an "id:". EventSource echoes the last id it
// saw back as Last-Event-ID when it reconnects, so we can replay what was missed.
app.MapGet("orders/realtime/with-replays", (
    OrderBroadcaster broadcaster,
    OrderEventBuffer eventBuffer,
    [FromHeader(Name = "Last-Event-ID")] string? lastEventId,
    CancellationToken cancellationToken) =>
{
    // Subscribe before reading the buffer, so an order published in between is
    // delivered live rather than falling through the gap.
    var subscription = broadcaster.Subscribe();

    async IAsyncEnumerable<SseItem<OrderPlacement>> StreamEvents()
    {
        using (subscription)
        {
            var replayedThrough = 0L;

            if (!string.IsNullOrWhiteSpace(lastEventId))
            {
                foreach (var missedEvent in eventBuffer.GetEventsAfter(lastEventId))
                {
                    replayedThrough = long.Parse(missedEvent.EventId!);
                    yield return missedEvent;
                }
            }

            await foreach (var item in subscription.Reader.ReadAllAsync(cancellationToken))
            {
                // Skip anything the replay above already covered.
                if (long.Parse(item.EventId!) <= replayedThrough)
                {
                    continue;
                }

                yield return item;
            }
        }
    }

    // Note: the SseItem<T> overload takes no eventType argument - the event type
    // travels on the item itself (set in OrderEventBuffer.Add). Passing one here
    // silently binds the plain IAsyncEnumerable<T> overload and serializes the
    // whole SseItem into "data:" instead of emitting an "id:" line.
    return TypedResults.ServerSentEvents(StreamEvents());
});

// Filtering happens inside the generator, so each client only sees its own orders.
// The article resolves the caller from an IUserContext behind .RequireAuthorization();
// this sample takes a query parameter to stay runnable with no auth setup.
app.MapGet("orders/realtime/mine", (
    string customerId,
    OrderBroadcaster broadcaster,
    CancellationToken cancellationToken) =>
{
    var subscription = broadcaster.Subscribe();

    async IAsyncEnumerable<OrderPlacement> StreamCustomerOrders()
    {
        using (subscription)
        {
            await foreach (var item in subscription.Reader.ReadAllAsync(cancellationToken))
            {
                if (item.Data!.CustomerId == customerId)
                {
                    yield return item.Data;
                }
            }
        }
    }

    return Results.ServerSentEvents(StreamCustomerOrders(), eventType: "orders");
});

app.MapPost("orders", (OrderPlacement order, OrderBroadcaster broadcaster) =>
{
    broadcaster.Publish(order);

    return TypedResults.Accepted($"orders/{order.OrderId}", order);
});

app.Run();

record OrderPlacement(string OrderId, string CustomerId, decimal Amount, DateTime PlacedAt);

// The article injects a single ChannelReader<OrderPlacement>. That is a competing
// consumer: with two browser tabs open, each order reaches only one of them. This
// broadcaster gives every subscriber its own channel so all of them see every order.
//
// It also stamps the event id once, at publish time, so the id is stable across
// subscribers and the replay buffer fills even while nobody is connected.
sealed class OrderBroadcaster(OrderEventBuffer eventBuffer)
{
    private readonly ConcurrentDictionary<Guid, Channel<SseItem<OrderPlacement>>> subscribers = new();

    public Subscription Subscribe()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateUnbounded<SseItem<OrderPlacement>>(new UnboundedChannelOptions
        {
            SingleReader = true
        });

        subscribers[id] = channel;

        return new Subscription(channel.Reader, () =>
        {
            subscribers.TryRemove(id, out _);
            channel.Writer.TryComplete();
        });
    }

    public void Publish(OrderPlacement order)
    {
        var item = eventBuffer.Add(order);

        foreach (var subscriber in subscribers.Values)
        {
            subscriber.Writer.TryWrite(item);
        }
    }

    public sealed class Subscription(ChannelReader<SseItem<OrderPlacement>> reader, Action unsubscribe) : IDisposable
    {
        public ChannelReader<SseItem<OrderPlacement>> Reader { get; } = reader;

        public void Dispose() => unsubscribe();
    }
}

// Keeps the most recent events so a reconnecting client can catch up. A real system
// would read this range back out of its event store instead.
sealed class OrderEventBuffer
{
    private const int Capacity = 50;

    private readonly Lock gate = new();
    private readonly Queue<(long Id, SseItem<OrderPlacement> Item)> events = new();
    private long nextId;

    public SseItem<OrderPlacement> Add(OrderPlacement order)
    {
        lock (gate)
        {
            var id = ++nextId;
            var item = new SseItem<OrderPlacement>(order, "orders")
            {
                EventId = id.ToString()
            };

            events.Enqueue((id, item));

            if (events.Count > Capacity)
            {
                events.Dequeue();
            }

            return item;
        }
    }

    public IReadOnlyList<SseItem<OrderPlacement>> GetEventsAfter(string lastEventId)
    {
        if (!long.TryParse(lastEventId, out var id))
        {
            return [];
        }

        lock (gate)
        {
            return events.Where(e => e.Id > id).Select(e => e.Item).ToArray();
        }
    }
}

// Fake traffic so the demo page shows something the moment it loads.
sealed class OrderProducer(OrderBroadcaster broadcaster) : BackgroundService
{
    private static readonly string[] Customers = ["alice", "bob", "carol"];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            broadcaster.Publish(new OrderPlacement(
                Guid.NewGuid().ToString("N")[..8],
                Customers[Random.Shared.Next(Customers.Length)],
                Math.Round((decimal)Random.Shared.NextDouble() * 500, 2),
                DateTime.UtcNow));
        }
    }
}
