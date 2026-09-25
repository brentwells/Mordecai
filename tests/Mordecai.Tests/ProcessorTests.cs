using Microsoft.Extensions.DependencyInjection;
using Mordecai.Tests.Fakes;

namespace Mordecai.Tests;

public sealed class ProcessorTests
{
    [Fact]
    public async Task APreProcessorRunsBeforeTheHandler()
    {
        var log = new RecordingLog();
        var handler = new FlaggedEchoHandler();

        await using var provider = Build(
            log,
            static cfg => cfg.AddOpenRequestPreProcessor(typeof(FirstPreProcessor<>)),
            services => services.AddSingleton<IRequestHandler<Echo, string>>(handler));

        await using var scope = provider.CreateAsyncScope();

        var response = await scope.ServiceProvider
            .GetRequiredService<ISender>()
            .Send(new Echo("hi"), TestContext.Current.CancellationToken);

        Assert.Equal("handled:hi", response);
        Assert.True(handler.Ran);
        Assert.Equal([$"pre-1:{nameof(Echo)}"], log.Entries);
    }

    [Fact]
    public async Task MultiplePreProcessorsAllRun()
    {
        var log = new RecordingLog();

        await using var provider = Build(
            log,
            static cfg => cfg
                .AddOpenRequestPreProcessor(typeof(FirstPreProcessor<>))
                .AddOpenRequestPreProcessor(typeof(SecondPreProcessor<>)),
            static services => services.AddTransient<IRequestHandler<Echo, string>, EchoHandler>());

        await using var scope = provider.CreateAsyncScope();

        await scope.ServiceProvider
            .GetRequiredService<ISender>()
            .Send(new Echo("hi"), TestContext.Current.CancellationToken);

        Assert.Equal(2, log.Entries.Count);
        Assert.Contains($"pre-1:{nameof(Echo)}", log.Entries);
        Assert.Contains($"pre-2:{nameof(Echo)}", log.Entries);
    }

    [Fact]
    public async Task AThrowingPreProcessorStopsTheHandlerFromRunning()
    {
        var log = new RecordingLog();
        var handler = new FlaggedEchoHandler();

        await using var provider = Build(
            log,
            static cfg => cfg.AddOpenRequestPreProcessor(typeof(ThrowingPreProcessor<>)),
            services => services.AddSingleton<IRequestHandler<Echo, string>>(handler));

        await using var scope = provider.CreateAsyncScope();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => scope.ServiceProvider
                .GetRequiredService<ISender>()
                .Send(new Echo("hi"), TestContext.Current.CancellationToken));

        Assert.Equal("pre-failed", exception.Message);
        Assert.False(handler.Ran);
    }

    [Fact]
    public async Task APostProcessorRunsAfterTheHandlerAndSeesTheResponse()
    {
        var log = new RecordingLog();

        await using var provider = Build(
            log,
            static cfg => cfg.AddOpenRequestPostProcessor(typeof(RecordingPostProcessor<,>)),
            static services => services.AddTransient<IRequestHandler<Echo, string>, EchoHandler>());

        await using var scope = provider.CreateAsyncScope();

        var response = await scope.ServiceProvider
            .GetRequiredService<ISender>()
            .Send(new Echo("hi"), TestContext.Current.CancellationToken);

        Assert.Equal("echo:hi", response);
        Assert.Equal(["post:echo:hi"], log.Entries);
    }

    [Fact]
    public async Task APostProcessorDoesNotRunWhenTheHandlerThrows()
    {
        var log = new RecordingLog();

        await using var provider = Build(
            log,
            static cfg => cfg.AddOpenRequestPostProcessor(typeof(RecordingPostProcessor<,>)),
            static services => services.AddTransient<IRequestHandler<Explode, string>, ExplodeHandler>());

        await using var scope = provider.CreateAsyncScope();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => scope.ServiceProvider
                .GetRequiredService<ISender>()
                .Send(new Explode(), TestContext.Current.CancellationToken));

        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task ProcessorsRunInsideTheBehaviorChain()
    {
        var log = new RecordingLog();

        await using var provider = Build(
            log,
            static cfg => cfg
                .AddOpenBehavior(typeof(OuterBehavior<,>))
                .AddOpenRequestPreProcessor(typeof(FirstPreProcessor<>))
                .AddOpenRequestPostProcessor(typeof(RecordingPostProcessor<,>)),
            static services => services.AddTransient<IRequestHandler<Echo, string>, EchoHandler>());

        await using var scope = provider.CreateAsyncScope();

        await scope.ServiceProvider
            .GetRequiredService<ISender>()
            .Send(new Echo("hi"), TestContext.Current.CancellationToken);

        // The user's behavior wraps everything; the pre-processor is the last thing before the
        // handler and the post-processor the first thing after it.
        Assert.Equal(
            ["outer:enter", $"pre-1:{nameof(Echo)}", "post:echo:hi", "outer:exit"],
            log.Entries);
    }

    [Fact]
    public async Task ProcessorBehaviorsAreNotRegisteredWhenNoProcessorIs()
    {
        var log = new RecordingLog();

        await using var provider = Build(
            log,
            static _ => { },
            static services => services.AddTransient<IRequestHandler<Echo, string>, EchoHandler>());

        await using var scope = provider.CreateAsyncScope();

        Assert.Empty(scope.ServiceProvider.GetServices<IPipelineBehavior<Echo, string>>());
    }

    [Fact]
    public async Task RegisteringOnlyAPreProcessorDoesNotAddThePostProcessorBehavior()
    {
        var log = new RecordingLog();

        await using var provider = Build(
            log,
            static cfg => cfg.AddOpenRequestPreProcessor(typeof(FirstPreProcessor<>)),
            static services => services.AddTransient<IRequestHandler<Echo, string>, EchoHandler>());

        await using var scope = provider.CreateAsyncScope();

        Assert.Single(scope.ServiceProvider.GetServices<IPipelineBehavior<Echo, string>>());
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
