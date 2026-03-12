using Aura.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace Aura.Tests;

public class RedisLogStreamServiceTests
{
    [Fact]
    public void StreamKey_FormatsCorrectly()
    {
        var runId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var key = RedisLogStreamService.StreamKey(runId);
        Assert.Equal("run:aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee:logs", key);
    }

    [Fact]
    public void ChannelKey_FormatsCorrectly()
    {
        var runId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var key = RedisLogStreamService.ChannelKey(runId);
        Assert.Equal("run:aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee:logs", key);
    }

    [Fact]
    public void NotifyKey_FormatsCorrectly()
    {
        var runId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var key = RedisLogStreamService.NotifyKey(runId);
        Assert.Equal("run:aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee:notify", key);
    }

    [Fact]
    public async Task PublishAsync_CallsStreamAddAndPublish()
    {
        var runId = Guid.NewGuid();
        var expectedStreamKey = RedisLogStreamService.StreamKey(runId);
        var expectedNotifyKey = RedisLogStreamService.NotifyKey(runId);

        var dbMock = new Mock<IDatabase>();
        dbMock
            .Setup(d => d.StreamAddAsync(
                It.Is<RedisKey>(k => k.ToString() == expectedStreamKey),
                It.Is<RedisValue>(f => f.ToString() == "msg"),
                It.Is<RedisValue>(v => v.ToString() == "hello world"),
                It.IsAny<RedisValue?>(),
                It.IsAny<int?>(),
                It.IsAny<bool>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync("1-0");

        var subscriberMock = new Mock<ISubscriber>();
        subscriberMock
            .Setup(s => s.PublishAsync(
                It.Is<RedisChannel>(c => c.ToString() == expectedNotifyKey),
                It.IsAny<RedisValue>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(1);

        var redisMock = new Mock<IConnectionMultiplexer>();
        redisMock.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(dbMock.Object);
        redisMock.Setup(r => r.GetSubscriber(It.IsAny<object>()))
            .Returns(subscriberMock.Object);

        var loggerMock = new Mock<ILogger<RedisLogStreamService>>();
        var service = new RedisLogStreamService(redisMock.Object, loggerMock.Object);

        await service.PublishAsync(runId, "hello world");

        dbMock.Verify(
            d => d.StreamAddAsync(
                It.Is<RedisKey>(k => k.ToString() == expectedStreamKey),
                It.Is<RedisValue>(f => f.ToString() == "msg"),
                It.Is<RedisValue>(v => v.ToString() == "hello world"),
                It.IsAny<RedisValue?>(),
                It.IsAny<int?>(),
                It.IsAny<bool>(),
                It.IsAny<CommandFlags>()),
            Times.Once);

        subscriberMock.Verify(
            s => s.PublishAsync(
                It.Is<RedisChannel>(c => c.ToString() == expectedNotifyKey),
                It.IsAny<RedisValue>(),
                It.IsAny<CommandFlags>()),
            Times.Once);
    }

    [Fact]
    public async Task PublishAsync_StreamEnd_SetsKeyExpiry()
    {
        var runId = Guid.NewGuid();
        var expectedStreamKey = RedisLogStreamService.StreamKey(runId);

        var dbMock = new Mock<IDatabase>();
        dbMock
            .Setup(d => d.StreamAddAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<RedisValue>(),
                It.IsAny<RedisValue?>(),
                It.IsAny<int?>(),
                It.IsAny<bool>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync("1-0");
        dbMock
            .Setup(d => d.KeyExpireAsync(
                It.Is<RedisKey>(k => k.ToString() == expectedStreamKey),
                It.Is<TimeSpan>(t => t == TimeSpan.FromHours(2)),
                It.IsAny<ExpireWhen>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        var subscriberMock = new Mock<ISubscriber>();
        subscriberMock
            .Setup(s => s.PublishAsync(It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(1);

        var redisMock = new Mock<IConnectionMultiplexer>();
        redisMock.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(dbMock.Object);
        redisMock.Setup(r => r.GetSubscriber(It.IsAny<object>()))
            .Returns(subscriberMock.Object);

        var loggerMock = new Mock<ILogger<RedisLogStreamService>>();
        var service = new RedisLogStreamService(redisMock.Object, loggerMock.Object);

        await service.PublishAsync(runId, RedisLogStreamService.StreamEndSentinel);

        dbMock.Verify(
            d => d.KeyExpireAsync(
                It.Is<RedisKey>(k => k.ToString() == expectedStreamKey),
                It.Is<TimeSpan>(t => t == TimeSpan.FromHours(2)),
                It.IsAny<ExpireWhen>(),
                It.IsAny<CommandFlags>()),
            Times.Once);
    }

    [Fact]
    public async Task PublishAsync_NonSentinel_DoesNotSetExpiry()
    {
        var runId = Guid.NewGuid();

        var dbMock = new Mock<IDatabase>();
        dbMock
            .Setup(d => d.StreamAddAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<RedisValue>(),
                It.IsAny<RedisValue?>(),
                It.IsAny<int?>(),
                It.IsAny<bool>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync("1-0");

        var subscriberMock = new Mock<ISubscriber>();
        subscriberMock
            .Setup(s => s.PublishAsync(It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(1);

        var redisMock = new Mock<IConnectionMultiplexer>();
        redisMock.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(dbMock.Object);
        redisMock.Setup(r => r.GetSubscriber(It.IsAny<object>()))
            .Returns(subscriberMock.Object);

        var loggerMock = new Mock<ILogger<RedisLogStreamService>>();
        var service = new RedisLogStreamService(redisMock.Object, loggerMock.Object);

        await service.PublishAsync(runId, "regular message");

        dbMock.Verify(
            d => d.KeyExpireAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<ExpireWhen>(),
                It.IsAny<CommandFlags>()),
            Times.Never);
    }

    [Fact]
    public async Task SubscribeAsync_WithHistory_ReturnsAllMessagesBeforeSentinel()
    {
        var runId = Guid.NewGuid();
        var streamKey = RedisLogStreamService.StreamKey(runId);

        var historyEntries = new StreamEntry[]
        {
            new("1-0", new[] { new NameValueEntry("msg", "line 1") }),
            new("2-0", new[] { new NameValueEntry("msg", "line 2") }),
            new("3-0", new[] { new NameValueEntry("msg", RedisLogStreamService.StreamEndSentinel) }),
        };

        var dbMock = new Mock<IDatabase>();
        dbMock
            .Setup(d => d.StreamRangeAsync(
                It.Is<RedisKey>(k => k.ToString() == streamKey),
                It.Is<RedisValue>(v => v.ToString() == "-"),
                It.Is<RedisValue>(v => v.ToString() == "+"),
                It.IsAny<int?>(),
                It.IsAny<Order>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(historyEntries);

        var redisMock = new Mock<IConnectionMultiplexer>();
        redisMock.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(dbMock.Object);

        var loggerMock = new Mock<ILogger<RedisLogStreamService>>();
        var service = new RedisLogStreamService(redisMock.Object, loggerMock.Object);

        var messages = new List<string>();
        await foreach (var msg in service.SubscribeAsync(runId))
        {
            messages.Add(msg);
        }

        Assert.Equal(2, messages.Count);
        Assert.Equal("line 1", messages[0]);
        Assert.Equal("line 2", messages[1]);
    }

    [Fact]
    public async Task PublishAsync_MultipleMessages_AllStoredInStream()
    {
        var runId = Guid.NewGuid();
        var expectedStreamKey = RedisLogStreamService.StreamKey(runId);
        var addedMessages = new List<string>();

        var dbMock = new Mock<IDatabase>();
        dbMock
            .Setup(d => d.StreamAddAsync(
                It.Is<RedisKey>(k => k.ToString() == expectedStreamKey),
                It.Is<RedisValue>(f => f.ToString() == "msg"),
                It.IsAny<RedisValue>(),
                It.IsAny<RedisValue?>(),
                It.IsAny<int?>(),
                It.IsAny<bool>(),
                It.IsAny<CommandFlags>()))
            .Callback<RedisKey, RedisValue, RedisValue, RedisValue?, int?, bool, CommandFlags>(
                (_, _, v, _, _, _, _) => addedMessages.Add(v.ToString()))
            .ReturnsAsync("1-0");
        dbMock
            .Setup(d => d.KeyExpireAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<ExpireWhen>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        var subscriberMock = new Mock<ISubscriber>();
        subscriberMock
            .Setup(s => s.PublishAsync(It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(1);

        var redisMock = new Mock<IConnectionMultiplexer>();
        redisMock.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(dbMock.Object);
        redisMock.Setup(r => r.GetSubscriber(It.IsAny<object>()))
            .Returns(subscriberMock.Object);

        var loggerMock = new Mock<ILogger<RedisLogStreamService>>();
        var service = new RedisLogStreamService(redisMock.Object, loggerMock.Object);

        await service.PublishAsync(runId, "line 1");
        await service.PublishAsync(runId, "line 2");
        await service.PublishAsync(runId, RedisLogStreamService.StreamEndSentinel);

        Assert.Equal(3, addedMessages.Count);
        Assert.Equal("line 1", addedMessages[0]);
        Assert.Equal("line 2", addedMessages[1]);
        Assert.Equal("[STREAM_END]", addedMessages[2]);
    }

    [Fact]
    public void StreamEndSentinel_IsExpectedValue()
    {
        Assert.Equal("[STREAM_END]", RedisLogStreamService.StreamEndSentinel);
    }
}
