using Microsoft.Extensions.DependencyInjection;
using Mordecai.Tests.Fakes;

namespace Mordecai.Tests;

public sealed class MediatorPublishTests
{
    [Fact]
    public async Task PublishInvokesEveryRegisteredHandler()
    {
        var log = new RecordingLog();

        await using var host = TestHost.Build(services =>
        {
            services.AddSingleton(log);
            services.AddTransient<INotificationHandler<Signal>, FirstSignalHandler>();
            services.AddTransient<INotificationHandler<Signal>, SecondSignalHandler>();
        });

        await host.Mediator.Publish(new Signal("go"), TestContext.Current.CancellationToken);

        // Membership, not order: the guide is explicit that handler order is not guaranteed, so a
        // test that pinned it would be asserting an implementation detail.
        Assert.Equal(2, log.Entries.Count);
        Assert.Contains("first:go", log.Entries);
        Assert.Contains("second:go", log.Entries);
    }

    [Fact]
    public async Task PublishWithNoHandlersIsANoOp()
    {
        await using var host = TestHost.Build(static _ => { });

        await host.Mediator.Publish(new Unheard(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PublishWithNullNotificationThrows()
    {
        await using var host = TestHost.Build(static _ => { });

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => host.Mediator.Publish<Signal>(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WeaklyTypedPublishReachesTheHandlers()
    {
        var log = new RecordingLog();

        await using var host = TestHost.Build(services =>
        {
            services.AddSingleton(log);
            services.AddTransient<INotificationHandler<Signal>, FirstSignalHandler>();
        });

        object notification = new Signal("boxed");

        await host.Mediator.Publish(notification, TestContext.Current.CancellationToken);

        Assert.Equal(["first:boxed"], log.Entries);
    }

    [Fact]
    public async Task WeaklyTypedPublishRejectsSomethingThatIsNotANotification()
    {
        await using var host = TestHost.Build(static _ => { });

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => host.Mediator.Publish(new NotAMessage(), TestContext.Current.CancellationToken));

        Assert.Equal("notification", exception.ParamName);
    }

    [Fact]
    public async Task PublishPassesTheCancellationTokenToTheHandlers()
    {
        CancellationToken observed = default;

        await using var host = TestHost.Build(services =>
            services.AddSingleton<INotificationHandler<Signal>>(
                new DelegateSignalHandler((_, ct) =>
                {
                    observed = ct;
                    return Task.CompletedTask;
                })));

        using var cts = new CancellationTokenSource();

        await host.Mediator.Publish(new Signal("token"), cts.Token);

        Assert.Equal(cts.Token, observed);
    }

    [Fact]
    public async Task PublishingThroughTheInterfaceStillReachesTheHandlers()
    {
        var log = new RecordingLog();

        await using var host = TestHost.Build(services =>
        {
            services.AddSingleton(log);
            services.AddTransient<INotificationHandler<Signal>, FirstSignalHandler>();
        });

        // TNotification is inferred as INotification here, not Signal. Resolving handlers from
        // the static type argument alone would find none and deliver to nobody, without error.
        INotification notification = new Signal("erased");

        await host.Mediator.Publish(notification, TestContext.Current.CancellationToken);

        Assert.Equal(["first:erased"], log.Entries);
    }

    [Fact]
    public async Task PublishingThroughAnIntermediateBaseTypeReachesTheHandlers()
    {
        var log = new RecordingLog();

        await using var host = TestHost.Build(services =>
        {
            services.AddSingleton(log);
            services.AddTransient<INotificationHandler<OrderShipped>, OrderShippedHandler>();
        });

        DomainEvent notification = new OrderShipped(42);

        await host.Mediator.Publish(notification, TestContext.Current.CancellationToken);

        Assert.Equal(["shipped:42"], log.Entries);
    }

    [Fact]
    public async Task PublishingFromAGenericHelperReachesTheHandlers()
    {
        var log = new RecordingLog();

        await using var host = TestHost.Build(services =>
        {
            services.AddSingleton(log);
            services.AddTransient<INotificationHandler<Signal>, FirstSignalHandler>();
        });

        // The shape that makes this easy to hit by accident: an outbox holding INotification
        // values, each handed to a helper constrained to INotification. T binds to INotification,
        // not to Signal, so the concrete type is erased before Publish ever sees it.
        IReadOnlyList<INotification> outbox = [new Signal("via-helper")];

        foreach (var pending in outbox)
        {
            await Raise(host.Mediator, pending, TestContext.Current.CancellationToken);
        }

        Assert.Equal(["first:via-helper"], log.Entries);

        static Task Raise<T>(IPublisher publisher, T notification, CancellationToken cancellationToken)
            where T : INotification
            => publisher.Publish(notification, cancellationToken);
    }

    [Fact]
    public async Task PublishingThroughTheInterfaceDeliversExactlyOncePerHandler()
    {
        var log = new RecordingLog();

        await using var host = TestHost.Build(services =>
        {
            services.AddSingleton(log);
            services.AddTransient<INotificationHandler<Signal>, FirstSignalHandler>();
            services.AddTransient<INotificationHandler<Signal>, SecondSignalHandler>();
        });

        INotification notification = new Signal("once");

        await host.Mediator.Publish(notification, TestContext.Current.CancellationToken);

        Assert.Equal(2, log.Entries.Count);
        Assert.Contains("first:once", log.Entries);
        Assert.Contains("second:once", log.Entries);
    }

    [Fact]
    public async Task NoHandlerIsRegisteredForTheStaticTypeItself()
    {
        var log = new RecordingLog();

        await using var host = TestHost.Build(services =>
        {
            services.AddSingleton(log);
            services.AddTransient<INotificationHandler<Signal>, FirstSignalHandler>();
        });

        // Pins the premise the fix rests on: nothing is registered against INotification, so the
        // erased call would genuinely have found zero handlers rather than being saved by luck.
        Assert.Empty(host.Services.GetServices<INotificationHandler<INotification>>());
    }

    private sealed class DelegateSignalHandler(Func<Signal, CancellationToken, Task> handle)
        : INotificationHandler<Signal>
    {
        public Task Handle(Signal notification, CancellationToken cancellationToken)
            => handle(notification, cancellationToken);
    }
}
