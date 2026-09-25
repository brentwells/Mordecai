using Microsoft.Extensions.DependencyInjection;
using Mordecai.Tests.Fakes;

namespace Mordecai.Tests;

public sealed class PipelineBehaviorTests
{
    [Fact]
    public async Task ASingleBehaviorRunsBeforeAndAfterTheHandler()
    {
        var log = new RecordingLog();

        await using var provider = Build(
            log,
            static cfg => cfg.AddOpenBehavior(typeof(OuterBehavior<,>)),
            static services => services.AddTransient<IRequestHandler<Echo, string>, EchoHandler>());

        await using var scope = provider.CreateAsyncScope();

        var response = await scope.ServiceProvider
            .GetRequiredService<ISender>()
            .Send(new Echo("hi"), TestContext.Current.CancellationToken);

        Assert.Equal("echo:hi", response);
        Assert.Equal(["outer:enter", "outer:exit"], log.Entries);
    }

    [Fact]
    public async Task TwoBehaviorsNestInRegistrationOrderOutermostFirst()
    {
        var log = new RecordingLog();

        await using var provider = Build(
            log,
            static cfg =>
            {
                cfg.AddOpenBehavior(typeof(OuterBehavior<,>));
                cfg.AddOpenBehavior(typeof(InnerBehavior<,>));
            },
            static services => services.AddTransient<IRequestHandler<Echo, string>, EchoHandler>());

        await using var scope = provider.CreateAsyncScope();

        await scope.ServiceProvider
            .GetRequiredService<ISender>()
            .Send(new Echo("hi"), TestContext.Current.CancellationToken);

        Assert.Equal(
            ["outer:enter", "inner:enter", "inner:exit", "outer:exit"],
            log.Entries);
    }

    [Fact]
    public async Task NotCallingNextStopsTheHandlerFromRunning()
    {
        var log = new RecordingLog();
        var handler = new FlaggedEchoHandler();

        await using var provider = Build(
            log,
            static cfg => cfg.AddBehavior<IPipelineBehavior<Echo, string>, ShortCircuitBehavior>(),
            services => services.AddSingleton<IRequestHandler<Echo, string>>(handler));

        await using var scope = provider.CreateAsyncScope();

        var response = await scope.ServiceProvider
            .GetRequiredService<ISender>()
            .Send(new Echo("hi"), TestContext.Current.CancellationToken);

        Assert.Equal("short-circuited", response);

        // The return value alone would pass even if the handler ran and its result was discarded.
        Assert.False(handler.Ran);
    }

    [Fact]
    public async Task ABehaviorCanReplaceTheResponse()
    {
        var log = new RecordingLog();

        await using var provider = Build(
            log,
            static cfg => cfg.AddBehavior<IPipelineBehavior<Echo, string>, RewritingBehavior>(),
            static services => services.AddTransient<IRequestHandler<Echo, string>, EchoHandler>());

        await using var scope = provider.CreateAsyncScope();

        var response = await scope.ServiceProvider
            .GetRequiredService<ISender>()
            .Send(new Echo("hi"), TestContext.Current.CancellationToken);

        Assert.Equal("[echo:hi]", response);
    }

    [Fact]
    public async Task AHandlerExceptionPropagatesAndBehaviorsSeeIt()
    {
        var log = new RecordingLog();

        await using var provider = Build(
            log,
            static cfg =>
            {
                cfg.AddOpenBehavior(typeof(ObservingBehavior<,>));
                cfg.AddOpenBehavior(typeof(OuterBehavior<,>));
            },
            static services => services.AddTransient<IRequestHandler<Explode, string>, ExplodeHandler>());

        await using var scope = provider.CreateAsyncScope();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => scope.ServiceProvider
                .GetRequiredService<ISender>()
                .Send(new Explode(), TestContext.Current.CancellationToken));

        Assert.Equal("handler-failed", exception.Message);
        Assert.Contains("caught:handler-failed", log.Entries);

        // The inner behavior entered but never exited, because the handler threw past it.
        Assert.Contains("outer:enter", log.Entries);
        Assert.DoesNotContain("outer:exit", log.Entries);
    }

    [Fact]
    public async Task ATokenSubstitutedByABehaviorReachesTheHandler()
    {
        var log = new RecordingLog();
        var behavior = new TokenSubstitutingBehavior();

        await using var provider = Build(
            log,
            static _ => { },
            services =>
            {
                services.AddTransient<IRequestHandler<ProbeToken, CancellationToken>, ProbeTokenHandler>();
                services.AddSingleton<IPipelineBehavior<ProbeToken, CancellationToken>>(behavior);
            });

        await using var scope = provider.CreateAsyncScope();

        using var caller = new CancellationTokenSource();

        var observed = await scope.ServiceProvider
            .GetRequiredService<ISender>()
            .Send(new ProbeToken(), caller.Token);

        Assert.Equal(behavior.SubstitutedToken, observed);
        Assert.NotEqual(caller.Token, observed);
    }

    [Fact]
    public async Task OpenAndClosedBehaviorsShareOneOrderedChain()
    {
        var log = new RecordingLog();

        await using var provider = Build(
            log,
            static cfg =>
            {
                cfg.AddOpenBehavior(typeof(OuterBehavior<,>));
                cfg.AddBehavior<IPipelineBehavior<Echo, string>, ClosedEchoBehavior>();
                cfg.AddOpenBehavior(typeof(InnerBehavior<,>));
            },
            static services => services.AddTransient<IRequestHandler<Echo, string>, EchoHandler>());

        await using var scope = provider.CreateAsyncScope();

        await scope.ServiceProvider
            .GetRequiredService<ISender>()
            .Send(new Echo("hi"), TestContext.Current.CancellationToken);

        Assert.Equal(
            ["outer:enter", "closed:enter", "inner:enter", "inner:exit", "closed:exit", "outer:exit"],
            log.Entries);
    }

    [Fact]
    public async Task AConstrainedBehaviorAppliesOnlyToMatchingRequests()
    {
        var log = new RecordingLog();

        await using var provider = Build(
            log,
            static cfg => cfg.AddOpenBehavior(typeof(AuditBehavior<,>)),
            static services =>
            {
                services.AddTransient<IRequestHandler<Echo, string>, EchoHandler>();
                services.AddTransient<IRequestHandler<AuditedEcho, string>, AuditedEchoHandler>();
            });

        await using var scope = provider.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await sender.Send(new Echo("plain"), TestContext.Current.CancellationToken);

        Assert.Empty(log.Entries);

        await sender.Send(new AuditedEcho("marked"), TestContext.Current.CancellationToken);

        Assert.Equal([$"audit:{nameof(AuditedEcho)}"], log.Entries);
    }

    [Fact]
    public async Task RepeatedSendsThroughAPipelineKeepTheSameOrder()
    {
        var log = new RecordingLog();

        await using var provider = Build(
            log,
            static cfg =>
            {
                cfg.AddOpenBehavior(typeof(OuterBehavior<,>));
                cfg.AddOpenBehavior(typeof(InnerBehavior<,>));
            },
            static services => services.AddTransient<IRequestHandler<Echo, string>, EchoHandler>());

        await using var scope = provider.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await sender.Send(new Echo("one"), TestContext.Current.CancellationToken);
        await sender.Send(new Echo("two"), TestContext.Current.CancellationToken);

        // The wrapper caches whether behaviors exist, so the second send must not lose them.
        Assert.Equal(
            [
                "outer:enter", "inner:enter", "inner:exit", "outer:exit",
                "outer:enter", "inner:enter", "inner:exit", "outer:exit",
            ],
            log.Entries);
    }

    [Fact]
    public async Task ARequestWithNoBehaviorsStillReachesItsHandler()
    {
        var log = new RecordingLog();

        await using var provider = Build(
            log,
            static _ => { },
            static services => services.AddTransient<IRequestHandler<Echo, string>, EchoHandler>());

        await using var scope = provider.CreateAsyncScope();

        var response = await scope.ServiceProvider
            .GetRequiredService<ISender>()
            .Send(new Echo("hi"), TestContext.Current.CancellationToken);

        Assert.Equal("echo:hi", response);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task ABehaviorConstrainedToAGenericMarkerAppliesOnlyToMatchingRequests()
    {
        var log = new RecordingLog();

        await using var provider = Build(
            log,
            static cfg => cfg.AddOpenBehavior(typeof(QueryOnlyBehavior<,>)),
            static services =>
            {
                services.AddTransient<IRequestHandler<QueryEcho, string>, QueryEchoHandler>();
                services.AddTransient<IRequestHandler<Echo, string>, EchoHandler>();
            });

        await using var scope = provider.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        // The constraint is `where TRequest : IQueryRequest<TResponse>`, referencing both type
        // parameters. The container has to honour that when it closes the open generic, or a
        // plain request would either pick the behavior up or fail to construct it at all.
        await sender.Send(new Echo("plain"), TestContext.Current.CancellationToken);

        Assert.Empty(log.Entries);

        await sender.Send(new QueryEcho("marked"), TestContext.Current.CancellationToken);

        Assert.Equal([$"query-only:{nameof(QueryEcho)}"], log.Entries);
    }

    [Fact]
    public async Task ABehaviorCanCallNextMoreThanOnceAndTheWholeChainReruns()
    {
        var log = new RecordingLog();
        var handler = new FlakyHandler();

        await using var provider = Build(
            log,
            static cfg =>
            {
                cfg.AddOpenBehavior(typeof(RetryingBehavior<,>));
                cfg.AddOpenBehavior(typeof(InnerBehavior<,>));
            },
            services => services.AddSingleton<IRequestHandler<Flaky, string>>(handler));

        await using var scope = provider.CreateAsyncScope();

        var response = await scope.ServiceProvider
            .GetRequiredService<ISender>()
            .Send(new Flaky(), TestContext.Current.CancellationToken);

        Assert.Equal("ok-on-3", response);
        Assert.Equal(3, handler.Calls);

        // Every attempt re-runs the whole downstream chain, not just the handler. That is what
        // makes "retry above the transaction behavior" give each attempt a fresh transaction.
        Assert.Equal(3, log.Entries.Count(entry => entry == "inner:enter"));
    }

    [Fact]
    public async Task ABehaviorConstrainedOnTheResponseCanShortCircuitWithATypedValue()
    {
        var log = new RecordingLog();

        await using var provider = Build(
            log,
            static cfg => cfg.AddOpenBehavior(typeof(OutcomeMappingBehavior<,>)),
            static services =>
            {
                services.AddTransient<IRequestHandler<RiskyOperation, Outcome>, RiskyOperationHandler>();
                services.AddTransient<IRequestHandler<Echo, string>, EchoHandler>();
            });

        await using var scope = provider.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        // An open behavior cannot fabricate a TResponse; the static abstract factory on the
        // constraint is what lets it turn an exception into a typed response instead of throwing.
        var outcome = await sender.Send(new RiskyOperation(), TestContext.Current.CancellationToken);

        Assert.Equal("domain-failure", outcome.Error);

        // string does not implement IOutcome<string>, so the behavior is filtered out entirely.
        Assert.Equal("echo:hi", await sender.Send(new Echo("hi"), TestContext.Current.CancellationToken));
        Assert.Equal([$"mapping:{nameof(RiskyOperation)}"], log.Entries);
    }

    [Fact]
    public async Task AnOpenBehaviorAppliesToRequestsThatReturnNothing()
    {
        var log = new RecordingLog();
        var handler = new CancelOrderHandler();

        await using var provider = Build(
            log,
            static cfg => cfg.AddOpenBehavior(typeof(OuterBehavior<,>)),
            services => services.AddSingleton<IRequestHandler<CancelOrder, Unit>>(handler));

        await using var scope = provider.CreateAsyncScope();

        // A void request has a TResponse of Unit, which an open behavior sees like any other.
        await scope.ServiceProvider
            .GetRequiredService<ISender>()
            .Send(new CancelOrder(7), TestContext.Current.CancellationToken);

        Assert.Equal([7], handler.Cancelled);
        Assert.Equal(["outer:enter", "outer:exit"], log.Entries);
    }

    private static ServiceProvider Build(
        RecordingLog log,
        Action<MordecaiConfiguration> configure,
        Action<IServiceCollection> register)
    {
        var services = new ServiceCollection();
        services.AddSingleton(log);
        register(services);
        services.AddMordecai(configure);

        return services.BuildServiceProvider();
    }
}
