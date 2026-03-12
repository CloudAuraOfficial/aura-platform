using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Aura.Core.Interfaces;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Aura.Infrastructure.Services;

/// <summary>
/// Implements real-time log streaming via Redis Streams + Pub/Sub hybrid.
/// Each run gets a dedicated stream key: "run:{runId}:logs".
/// Messages are persisted via XADD so late subscribers can replay history.
/// A pub/sub channel "run:{runId}:notify" is used as a wake-up signal for live tailing.
/// A sentinel message "[STREAM_END]" signals run completion.
/// </summary>
public sealed class RedisLogStreamService : ILogStreamService, IDisposable
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisLogStreamService> _logger;

    public const string StreamEndSentinel = "[STREAM_END]";
    private static readonly TimeSpan StreamExpiry = TimeSpan.FromHours(2);

    public RedisLogStreamService(IConnectionMultiplexer redis, ILogger<RedisLogStreamService> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task PublishAsync(Guid runId, string message, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var streamKey = StreamKey(runId);

        // Persist the message in a Redis Stream
        await db.StreamAddAsync(streamKey, "msg", message);

        // Set TTL on the stream after writing the sentinel
        if (message == StreamEndSentinel)
        {
            await db.KeyExpireAsync(streamKey, StreamExpiry);
        }

        // Pub/sub wake-up signal for live tailers
        var subscriber = _redis.GetSubscriber();
        await subscriber.PublishAsync(RedisChannel.Literal(NotifyKey(runId)), "new");
    }

    public async IAsyncEnumerable<string> SubscribeAsync(
        Guid runId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var streamKey = StreamKey(runId);
        var lastId = "0-0";

        // Step 1: Replay all existing messages from the stream
        var history = await db.StreamRangeAsync(streamKey, "-", "+");
        if (history is { Length: > 0 })
        {
            foreach (var entry in history)
            {
                lastId = entry.Id!;
                var msg = entry["msg"].ToString();
                if (msg == StreamEndSentinel)
                    yield break;
                yield return msg;
            }
        }

        // Step 2: Tail new messages using pub/sub as wake-up + XREAD for data
        var notifyChannel = NotifyKey(runId);
        var subscriber = _redis.GetSubscriber();

        var signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        var queue = await subscriber.SubscribeAsync(RedisChannel.Literal(notifyChannel));
        queue.OnMessage(_ => signal.Writer.TryWrite(true));

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Read new entries since our last seen ID
                var entries = await db.StreamRangeAsync(streamKey, $"({lastId}", "+");
                if (entries is { Length: > 0 })
                {
                    foreach (var entry in entries)
                    {
                        lastId = entry.Id!;
                        var msg = entry["msg"].ToString();
                        if (msg == StreamEndSentinel)
                            yield break;
                        yield return msg;
                    }
                }

                // Wait for a wake-up notification or timeout (poll fallback)
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    await signal.Reader.ReadAsync(cts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Timeout — poll again (handles missed pub/sub edge cases)
                }
            }
        }
        finally
        {
            await queue.UnsubscribeAsync();
        }
    }

    internal static string StreamKey(Guid runId) => $"run:{runId}:logs";
    internal static string NotifyKey(Guid runId) => $"run:{runId}:notify";

    // Keep backward compat — ChannelKey now points to the stream key
    internal static string ChannelKey(Guid runId) => StreamKey(runId);

    public void Dispose()
    {
        // Connection lifetime managed by DI; nothing to dispose here
    }
}
