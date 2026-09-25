using Mordecai.Tests.Fakes;

namespace Mordecai.Tests;

public sealed class PublisherStrategyTests
{
    private static readonly Signal Notification = new("n");

    [Fact]
    public async Task SequentialRunsHandlersInOrder()
    {
        var log = new RecordingLog();
        var publisher = new SequentialPublisher();

        await publisher.Publish(
            [Logging(log, "a"), Logging(log, "b"), Logging(log, "c")],
            Notification,
            TestContext.Current.CancellationToken);

        Assert.Equal(["a", "b", "c"], log.Entries);
    }

    [Fact]
    public async Task SequentialStopsAfterAThrowingHandler()
    {
        var log = new RecordingLog();
        var publisher = new SequentialPublisher();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => publisher.Publish(
                [Logging(log, "a"), Throwing(log, "b"), Logging(log, "c")],
                Notification,
                TestContext.Current.CancellationToken));

        Assert.Equal("boom:b", exception.Message);
        Assert.Equal(["a", "b"], log.Entries);
    }

    [Fact]
    public async Task SequentialWaitsForEachHandlerBeforeStartingTheNext()
    {
        var log = new RecordingLog();
        var publisher = new SequentialPublisher();

        await publisher.Publish(
            [Delayed(log, "a"), Delayed(log, "b")],
            Notification,
            TestContext.Current.CancellationToken);

        Assert.Equal(["a:start", "a:end", "b:start", "b:end"], log.Entries);
    }

    [Fact]
    public async Task ParallelRunsEveryHandlerEvenWhenOneThrows()
    {
        var log = new RecordingLog();
        var publisher = new ParallelPublisher();

        var exception = await Assert.ThrowsAsync<AggregateException>(
            () => publisher.Publish(
                [Logging(log, "a"), Throwing(log, "b"), Logging(log, "c")],
                Notification,
                TestContext.Current.CancellationToken));

        Assert.Single(exception.InnerExceptions);
        Assert.Contains("a", log.Entries);
        Assert.Contains("b", log.Entries);
        Assert.Contains("c", log.Entries);
    }

    [Fact]
    public async Task ParallelAggregatesEveryFailure()
    {
        var log = new RecordingLog();
        var publisher = new ParallelPublisher();

        var exception = await Assert.ThrowsAsync<AggregateException>(
            () => publisher.Publish(
                [Throwing(log, "a"), Logging(log, "b"), Throwing(log, "c")],
                Notification,
                TestContext.Current.CancellationToken));

        Assert.Equal(2, exception.InnerExceptions.Count);
        Assert.Equal(3, log.Entries.Count);
    }

    [Fact]
    public async Task ParallelSurvivesAHandlerThatThrowsSynchronously()
    {
        var log = new RecordingLog();
        var publisher = new ParallelPublisher();

        // A callback that throws before returning a task would abort the loop that starts the
        // handlers, so the ones after it would never run at all.
        var throwsBeforeReturning = new NotificationHandlerExecutor(
            new object(),
            (_, _) => throw new InvalidOperationException("sync"));

        var exception = await Assert.ThrowsAsync<AggregateException>(
            () => publisher.Publish(
                [throwsBeforeReturning, Logging(log, "after")],
                Notification,
                TestContext.Current.CancellationToken));

        Assert.Single(exception.InnerExceptions);
        Assert.Equal(["after"], log.Entries);
    }

    [Fact]
    public async Task BothPublishersAcceptAnEmptyHandlerSet()
    {
        await new SequentialPublisher().Publish([], Notification, TestContext.Current.CancellationToken);
        await new ParallelPublisher().Publish([], Notification, TestContext.Current.CancellationToken);
    }

    private static NotificationHandlerExecutor Logging(RecordingLog log, string name)
        => new(new object(), (_, _) =>
        {
            log.Add(name);
            return Task.CompletedTask;
        });

    private static NotificationHandlerExecutor Throwing(RecordingLog log, string name)
        => new(new object(), async (_, _) =>
        {
            log.Add(name);
            await Task.Yield();
            throw new InvalidOperationException($"boom:{name}");
        });

    private static NotificationHandlerExecutor Delayed(RecordingLog log, string name)
        => new(new object(), async (_, ct) =>
        {
            log.Add($"{name}:start");
            await Task.Delay(20, ct);
            log.Add($"{name}:end");
        });
}
