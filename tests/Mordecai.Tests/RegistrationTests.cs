using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Mordecai.Internal;
using Mordecai.Tests.Scanning;

namespace Mordecai.Tests;

public sealed class RegistrationTests
{
    private static readonly Assembly ScanTarget = typeof(ScanTargetMarker).Assembly;

    [Fact]
    public async Task AddMordecaiWithNoConfigurationRegistersTheMediatorAndNoHandlers()
    {
        var services = new ServiceCollection();
        services.AddMordecai();

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        Assert.NotNull(scope.ServiceProvider.GetService<IMediator>());
        Assert.NotNull(scope.ServiceProvider.GetService<ISender>());
        Assert.NotNull(scope.ServiceProvider.GetService<IPublisher>());
        Assert.Null(scope.ServiceProvider.GetService<IRequestHandler<Ordered, string>>());
    }

    [Fact]
    public async Task ScanningDiscoversRequestStreamAndNotificationHandlers()
    {
        await using var provider = BuildScanned();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;

        Assert.IsType<OrderedHandler>(services.GetService<IRequestHandler<Ordered, string>>());
        Assert.IsType<VoidJobHandler>(services.GetService<IRequestHandler<VoidJob, Unit>>());
        Assert.IsType<TickerHandler>(services.GetService<IStreamRequestHandler<Ticker, int>>());
        Assert.Equal(2, services.GetServices<INotificationHandler<Beeped>>().Count());
    }

    [Fact]
    public async Task DiscoveredHandlersAreReachableThroughTheMediator()
    {
        var sink = new Sink();

        await using var provider = BuildScanned(sink);
        await using var scope = provider.CreateAsyncScope();

        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        Assert.Equal("ordered:7", await sender.Send(new Ordered(7), TestContext.Current.CancellationToken));
        Assert.Equal(
            Unit.Value,
            await sender.Send(new VoidJob("nightly"), TestContext.Current.CancellationToken));
        Assert.Equal(["ordered:7", "job:nightly"], sink.Entries);
    }

    [Fact]
    public async Task AnAssemblyRegisteredTwiceFiresItsNotificationHandlersOnce()
    {
        var sink = new Sink();

        var services = new ServiceCollection();
        services.AddSingleton<IScanSink>(sink);
        services.AddMordecai(cfg => cfg
            .RegisterServicesFromAssemblyContaining<ScanTargetMarker>()
            .RegisterServicesFromAssembly(ScanTarget)
            .RegisterServicesFromAssembliesContaining(typeof(OtherScanTargetMarker)));

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        await scope.ServiceProvider
            .GetRequiredService<IPublisher>()
            .Publish(new Beeped("once"), TestContext.Current.CancellationToken);

        // Two handlers, one delivery each. Without deduplication by assembly identity this would
        // be six entries, and the bug would only show up as double-sent email in production.
        Assert.Equal(2, sink.Entries.Count);
        Assert.Contains("beep-1:once", sink.Entries);
        Assert.Contains("beep-2:once", sink.Entries);
    }

    [Fact]
    public async Task BothNotificationHandlersForOneNotificationAreRegistered()
    {
        await using var provider = BuildScanned();
        await using var scope = provider.CreateAsyncScope();

        var handlers = scope.ServiceProvider.GetServices<INotificationHandler<Beeped>>().ToList();

        Assert.Contains(handlers, handler => handler is FirstBeepHandler);
        Assert.Contains(handlers, handler => handler is SecondBeepHandler);
    }

    [Fact]
    public void TwoHandlersForOneRequestThrowNamingBoth()
    {
        var exception = Assert.Throws<DuplicateHandlerException>(
            () => ScanTypes(typeof(FirstContestedHandler), typeof(SecondContestedHandler)));

        Assert.Equal(typeof(Contested), exception.RequestType);
        Assert.Equal(typeof(FirstContestedHandler), exception.FirstHandlerType);
        Assert.Equal(typeof(SecondContestedHandler), exception.SecondHandlerType);
        Assert.Contains(nameof(FirstContestedHandler), exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(SecondContestedHandler), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoStreamHandlersForOneStreamRequestAlsoThrow()
    {
        Assert.Throws<DuplicateHandlerException>(
            () => ScanTypes(typeof(FirstContestedStreamHandler), typeof(SecondContestedStreamHandler)));
    }

    [Fact]
    public void AnOpenGenericHandlerIsRejected()
    {
        var exception = Assert.Throws<NotSupportedException>(
            () => ScanTypes(typeof(OpenGenericHandler<>)));

        Assert.Contains("open generic handler", exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(OpenGenericHandler<object>), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AbstractHandlersAreSkipped()
    {
        var services = new ServiceCollection();

        ScanTypes(services, typeof(AbstractOrderedHandler));

        Assert.Empty(services);
    }

    [Fact]
    public async Task TheThreeInterfacesResolveToOneMediatorWithinAScope()
    {
        var services = new ServiceCollection();
        services.AddMordecai(static cfg => cfg.Lifetime = ServiceLifetime.Scoped);

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var publisher = scope.ServiceProvider.GetRequiredService<IPublisher>();

        Assert.Same(mediator, sender);
        Assert.Same(mediator, publisher);
    }

    [Fact]
    public async Task TheThreeInterfacesAllForwardToTheSameImplementation()
    {
        // Under the default transient lifetime the instances differ by definition; what must hold
        // is that all three are the one Mediator registration, not three independent services.
        var services = new ServiceCollection();
        services.AddMordecai();

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        Assert.IsType<Mediator>(scope.ServiceProvider.GetRequiredService<IMediator>());
        Assert.IsType<Mediator>(scope.ServiceProvider.GetRequiredService<ISender>());
        Assert.IsType<Mediator>(scope.ServiceProvider.GetRequiredService<IPublisher>());
    }

    [Theory]
    [InlineData(ServiceLifetime.Transient)]
    [InlineData(ServiceLifetime.Scoped)]
    [InlineData(ServiceLifetime.Singleton)]
    public void TheConfiguredLifetimeIsHonoured(ServiceLifetime lifetime)
    {
        var services = new ServiceCollection();
        services.AddMordecai(cfg =>
        {
            cfg.Lifetime = lifetime;
            cfg.RegisterServicesFromAssemblyContaining<ScanTargetMarker>();
        });

        Assert.Equal(lifetime, DescriptorFor(services, typeof(IRequestHandler<Ordered, string>)).Lifetime);
        Assert.Equal(lifetime, DescriptorFor(services, typeof(Mediator)).Lifetime);
        Assert.Equal(lifetime, DescriptorFor(services, typeof(ISender)).Lifetime);
    }

    [Fact]
    public void LifetimeIsReadAfterTheWholeConfigurationCallbackHasRun()
    {
        var services = new ServiceCollection();

        // The behavior is added before Lifetime is assigned; the assignment must still win.
        services.AddMordecai(static cfg =>
        {
            cfg.AddOpenBehavior(typeof(NoOpBehavior<,>));
            cfg.Lifetime = ServiceLifetime.Singleton;
        });

        Assert.Equal(
            ServiceLifetime.Singleton,
            DescriptorFor(services, typeof(IPipelineBehavior<,>)).Lifetime);
    }

    [Fact]
    public void AnExplicitBehaviorLifetimeOverridesTheConfiguredOne()
    {
        var services = new ServiceCollection();
        services.AddMordecai(static cfg => cfg
            .AddOpenBehavior(typeof(NoOpBehavior<,>), ServiceLifetime.Singleton));

        Assert.Equal(
            ServiceLifetime.Singleton,
            DescriptorFor(services, typeof(IPipelineBehavior<,>)).Lifetime);
    }

    [Fact]
    public async Task TheConfiguredNotificationPublisherIsUsed()
    {
        var services = new ServiceCollection();
        services.AddMordecai(static cfg => cfg.NotificationPublisher = new ParallelPublisher());

        await using var provider = services.BuildServiceProvider();

        Assert.IsType<ParallelPublisher>(provider.GetRequiredService<INotificationPublisher>());
    }

    [Fact]
    public async Task ThrowOnMissingHandlerCanBeTurnedOff()
    {
        var services = new ServiceCollection();
        services.AddMordecai(static cfg => cfg.ThrowOnMissingHandler = false);

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        Assert.Null(await sender.Send(new Ordered(1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ThrowOnMissingHandlerIsOnByDefault()
    {
        var services = new ServiceCollection();
        services.AddMordecai();

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await Assert.ThrowsAsync<HandlerNotFoundException>(
            () => sender.Send(new Ordered(1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AddMordecaiRejectsNullArguments()
    {
        Assert.Throws<ArgumentNullException>(
            () => MordecaiServiceCollectionExtensions.AddMordecai(null!));

        Assert.Throws<ArgumentNullException>(
            () => new ServiceCollection().AddMordecai(null!));
    }

    private static ServiceProvider BuildScanned(IScanSink? sink = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(sink ?? new Sink());
        services.AddMordecai(static cfg => cfg.RegisterServicesFromAssemblyContaining<ScanTargetMarker>());

        return services.BuildServiceProvider();
    }

    private static ServiceDescriptor DescriptorFor(IServiceCollection services, Type serviceType)
        => services.Single(descriptor => descriptor.ServiceType == serviceType);

    private static void ScanTypes(params Type[] types) => ScanTypes(new ServiceCollection(), types);

    private static void ScanTypes(IServiceCollection services, params Type[] types)
        => HandlerScanner.ScanTypes(services, types, ServiceLifetime.Transient, []);

    private sealed class Sink : IScanSink
    {
        private readonly ConcurrentQueue<string> _entries = new();

        public void Record(string entry) => _entries.Enqueue(entry);

        public IReadOnlyList<string> Entries => [.. _entries];
    }

    private sealed record Contested : IRequest<string>;

    private sealed class FirstContestedHandler : IRequestHandler<Contested, string>
    {
        public Task<string> Handle(Contested request, CancellationToken cancellationToken)
            => Task.FromResult("first");
    }

    private sealed class SecondContestedHandler : IRequestHandler<Contested, string>
    {
        public Task<string> Handle(Contested request, CancellationToken cancellationToken)
            => Task.FromResult("second");
    }

    private sealed record ContestedStream : IStreamRequest<string>;

    private sealed class FirstContestedStreamHandler : IStreamRequestHandler<ContestedStream, string>
    {
        public IAsyncEnumerable<string> Handle(ContestedStream request, CancellationToken cancellationToken)
            => AsyncEnumerable.Empty<string>();
    }

    private sealed class SecondContestedStreamHandler : IStreamRequestHandler<ContestedStream, string>
    {
        public IAsyncEnumerable<string> Handle(ContestedStream request, CancellationToken cancellationToken)
            => AsyncEnumerable.Empty<string>();
    }

    private sealed class OpenGenericHandler<TIgnored> : IRequestHandler<Contested, string>
    {
        public Task<string> Handle(Contested request, CancellationToken cancellationToken)
            => Task.FromResult(typeof(TIgnored).Name);
    }

    private sealed class NoOpBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
        where TRequest : notnull
    {
        public Task<TResponse> Handle(
            TRequest request,
            RequestHandlerDelegate<TResponse> next,
            CancellationToken cancellationToken)
            => next(cancellationToken);
    }
}
