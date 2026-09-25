using Microsoft.Extensions.DependencyInjection;
using Mordecai.Tests.Fakes;

namespace Mordecai.Tests;

public sealed class StreamingTests
{
    [Fact]
    public async Task AStreamHandlerYieldsAllItsItemsInOrder()
    {
        var handler = new CountdownHandler();

        await using var provider = Build(new RecordingLog(), static _ => { }, handler);
        await using var scope = provider.CreateAsyncScope();

        var items = new List<int>();

        await foreach (var item in scope.ServiceProvider
            .GetRequiredService<ISender>()
            .CreateStream(new Countdown(4), TestContext.Current.CancellationToken))
        {
            items.Add(item);
        }

        Assert.Equal([0, 1, 2, 3], items);
        Assert.True(handler.RanToCompletion);
    }

    [Fact]
    public async Task TheCallersTokenReachesTheHandler()
    {
        var handler = new CountdownHandler();

        await using var provider = Build(new RecordingLog(), static _ => { }, handler);
        await using var scope = provider.CreateAsyncScope();

        using var cts = new CancellationTokenSource();

        await foreach (var _ in scope.ServiceProvider
            .GetRequiredService<ISender>()
            .CreateStream(new Countdown(1), cts.Token))
        {
            // Drain.
        }

        Assert.Equal(cts.Token, handler.ObservedToken);
    }

    [Fact]
    public async Task CancellingMidEnumerationStopsTheStream()
    {
        var handler = new CountdownHandler();

        await using var provider = Build(new RecordingLog(), static _ => { }, handler);
        await using var scope = provider.CreateAsyncScope();

        using var cts = new CancellationTokenSource();
        var seen = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in scope.ServiceProvider
                .GetRequiredService<ISender>()
                .CreateStream(new Countdown(100), cts.Token))
            {
                if (++seen == 3)
                {
                    await cts.CancelAsync();
                }
            }
        });

        Assert.Equal(3, seen);
        Assert.False(handler.RanToCompletion);
    }

    [Fact]
    public async Task ATokenSuppliedThroughWithCancellationAlsoReachesTheHandler()
    {
        var handler = new CountdownHandler();

        await using var provider = Build(new RecordingLog(), static _ => { }, handler);
        await using var scope = provider.CreateAsyncScope();

        using var cts = new CancellationTokenSource();
        var seen = 0;

        // This is the [EnumeratorCancellation] path: this token arrives at GetAsyncEnumerator
        // rather than as an argument to CreateStream, and has to be combined with the one already
        // captured. It fails silently when the plumbing is wrong.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in scope.ServiceProvider
                .GetRequiredService<ISender>()
                .CreateStream(new Countdown(100), TestContext.Current.CancellationToken)
                .WithCancellation(cts.Token))
            {
                if (++seen == 2)
                {
                    await cts.CancelAsync();
                }
            }
        });

        Assert.Equal(2, seen);
    }

    [Fact]
    public async Task AStreamBehaviorObservesEveryElement()
    {
        var log = new RecordingLog();

        await using var provider = Build(
            log,
            static cfg => cfg.AddOpenStreamBehavior(typeof(RecordingStreamBehavior<,>)),
            new CountdownHandler());

        await using var scope = provider.CreateAsyncScope();

        await foreach (var _ in scope.ServiceProvider
            .GetRequiredService<ISender>()
            .CreateStream(new Countdown(3), TestContext.Current.CancellationToken))
        {
            // Drain.
        }

        Assert.Equal(
            ["stream:enter", "stream:item:0", "stream:item:1", "stream:item:2", "stream:exit"],
            log.Entries);
    }

    [Fact]
    public async Task BreakingOutEarlyDisposesTheHandlerAndSkipsTheBehaviorsTrailingCode()
    {
        var log = new RecordingLog();
        var handler = new CountdownHandler();

        await using var provider = Build(
            log,
            static cfg => cfg.AddOpenStreamBehavior(typeof(RecordingStreamBehavior<,>)),
            handler);

        await using var scope = provider.CreateAsyncScope();

        await foreach (var item in scope.ServiceProvider
            .GetRequiredService<ISender>()
            .CreateStream(new Countdown(100), TestContext.Current.CancellationToken))
        {
            if (item == 1)
            {
                break;
            }
        }

        Assert.True(handler.Disposed);
        Assert.False(handler.RanToCompletion);
        Assert.DoesNotContain("stream:exit", log.Entries);
        Assert.Equal(["stream:enter", "stream:item:0", "stream:item:1"], log.Entries);
    }

    [Fact]
    public async Task CreateStreamForAnUnregisteredRequestThrows()
    {
        await using var provider = Build(new RecordingLog(), static _ => { }, new CountdownHandler());
        await using var scope = provider.CreateAsyncScope();

        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        Assert.Throws<HandlerNotFoundException>(
            () => sender.CreateStream(new UnhandledStream(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ARequestBehaviorDoesNotRunForAStreamRequest()
    {
        var log = new RecordingLog();

        await using var provider = Build(
            log,
            static cfg => cfg.AddOpenBehavior(typeof(OuterBehavior<,>)),
            new CountdownHandler());

        await using var scope = provider.CreateAsyncScope();

        await foreach (var _ in scope.ServiceProvider
            .GetRequiredService<ISender>()
            .CreateStream(new Countdown(2), TestContext.Current.CancellationToken))
        {
            // Drain.
        }

        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task AStreamBehaviorDoesNotRunForARequest()
    {
        var log = new RecordingLog();

        var services = new ServiceCollection();
        services.AddSingleton(log);
        services.AddTransient<IRequestHandler<Echo, string>, EchoHandler>();
        services.AddMordecai(static cfg => cfg.AddOpenStreamBehavior(typeof(RecordingStreamBehavior<,>)));

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        await scope.ServiceProvider
            .GetRequiredService<ISender>()
            .Send(new Echo("hi"), TestContext.Current.CancellationToken);

        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task WeaklyTypedCreateStreamYieldsTheSameItems()
    {
        await using var provider = Build(new RecordingLog(), static _ => { }, new CountdownHandler());
        await using var scope = provider.CreateAsyncScope();

        object request = new Countdown(3);
        var items = new List<object?>();

        await foreach (var item in scope.ServiceProvider
            .GetRequiredService<ISender>()
            .CreateStream(request, TestContext.Current.CancellationToken))
        {
            items.Add(item);
        }

        Assert.Equal<object?>([0, 1, 2], items);
    }

    [Fact]
    public async Task WeaklyTypedCreateStreamRejectsSomethingThatIsNotAStreamRequest()
    {
        await using var provider = Build(new RecordingLog(), static _ => { }, new CountdownHandler());
        await using var scope = provider.CreateAsyncScope();

        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        Assert.Throws<ArgumentException>(
            () => sender.CreateStream(new NotAMessage(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RepeatedStreamsReuseTheCachedWrapper()
    {
        var log = new RecordingLog();

        await using var provider = Build(
            log,
            static cfg => cfg.AddOpenStreamBehavior(typeof(RecordingStreamBehavior<,>)),
            new CountdownHandler());

        await using var scope = provider.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        for (var round = 0; round < 2; round++)
        {
            await foreach (var _ in sender.CreateStream(new Countdown(1), TestContext.Current.CancellationToken))
            {
                // Drain.
            }
        }

        Assert.Equal(
            ["stream:enter", "stream:item:0", "stream:exit", "stream:enter", "stream:item:0", "stream:exit"],
            log.Entries);
    }

    private static ServiceProvider Build(
        RecordingLog log,
        Action<MordecaiConfiguration> configure,
        CountdownHandler handler)
    {
        var services = new ServiceCollection();
        services.AddSingleton(log);
        services.AddSingleton<IStreamRequestHandler<Countdown, int>>(handler);
        services.AddMordecai(configure);

        return services.BuildServiceProvider();
    }
}
