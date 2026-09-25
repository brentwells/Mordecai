using Microsoft.Extensions.DependencyInjection;
using Mordecai.Internal;
using Mordecai.Tests.Fakes;

namespace Mordecai.Tests;

public sealed class MediatorSendTests
{
    [Fact]
    public async Task SendReturnsTheHandlerResponse()
    {
        await using var host = TestHost.Build(static services =>
            services.AddTransient<IRequestHandler<Echo, string>, EchoHandler>());

        var response = await host.Mediator.Send(new Echo("hi"), TestContext.Current.CancellationToken);

        Assert.Equal("echo:hi", response);
    }

    [Fact]
    public async Task SendThrowsWhenNoHandlerIsRegistered()
    {
        await using var host = TestHost.Build(static _ => { });

        var exception = await Assert.ThrowsAsync<HandlerNotFoundException>(
            () => host.Mediator.Send(new Unhandled(), TestContext.Current.CancellationToken));

        Assert.Equal(typeof(Unhandled), exception.RequestType);
        Assert.Contains(nameof(Unhandled), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendPassesTheCancellationTokenToTheHandler()
    {
        await using var host = TestHost.Build(static services =>
            services.AddTransient<IRequestHandler<ProbeToken, CancellationToken>, ProbeTokenHandler>());

        using var cts = new CancellationTokenSource();

        var observed = await host.Mediator.Send(new ProbeToken(), cts.Token);

        Assert.Equal(cts.Token, observed);
    }

    [Fact]
    public async Task SendWithNullRequestThrows()
    {
        await using var host = TestHost.Build(static _ => { });

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => host.Mediator.Send<string>(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WeaklyTypedSendDispatchesToTheHandler()
    {
        await using var host = TestHost.Build(static services =>
            services.AddTransient<IRequestHandler<Echo, string>, EchoHandler>());

        object request = new Echo("boxed");

        var response = await host.Mediator.Send(request, TestContext.Current.CancellationToken);

        Assert.Equal("echo:boxed", response);
    }

    [Fact]
    public async Task WeaklyTypedSendRejectsSomethingThatIsNotARequest()
    {
        await using var host = TestHost.Build(static _ => { });

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => host.Mediator.Send(new NotAMessage(), TestContext.Current.CancellationToken));

        Assert.Contains(nameof(NotAMessage), exception.Message, StringComparison.Ordinal);
        Assert.Equal("request", exception.ParamName);
    }

    [Fact]
    public async Task WeaklyTypedSendWithNullRequestThrows()
    {
        await using var host = TestHost.Build(static _ => { });

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => host.Mediator.Send(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task VoidRequestResolvesAgainstTheTwoArgumentHandlerInterface()
    {
        var handler = new CancelOrderHandler();

        await using var host = TestHost.Build(services =>
            services.AddSingleton<IRequestHandler<CancelOrder, Unit>>(handler));

        var response = await host.Mediator.Send(new CancelOrder(42), TestContext.Current.CancellationToken);

        Assert.Equal(Unit.Value, response);
        Assert.Equal([42], handler.Cancelled);
    }

    [Fact]
    public void WrapperCacheReturnsTheSameInstanceForRepeatedSends()
    {
        var cache = new HandlerWrapperCache();

        var first = cache.GetRequestWrapper<string>(typeof(Echo));
        var second = cache.GetRequestWrapper<string>(typeof(Echo));

        Assert.Same(first, second);
    }

    [Fact]
    public async Task RepeatedSendsOfOneRequestTypeReuseOneWrapper()
    {
        var cache = new HandlerWrapperCache();

        await using var host = TestHost.Build(services =>
        {
            services.AddSingleton(cache);
            services.AddTransient<IRequestHandler<Echo, string>, EchoHandler>();
        });

        await host.Mediator.Send(new Echo("one"), TestContext.Current.CancellationToken);
        await host.Mediator.Send(new Echo("two"), TestContext.Current.CancellationToken);

        // A second wrapper would be a different object; the cache handing back the one already
        // built is what keeps the send path free of reflection after the first call.
        Assert.Same(
            cache.GetRequestWrapper<string>(typeof(Echo)),
            cache.GetRequestWrapper<string>(typeof(Echo)));
    }

    [Fact]
    public void ConstructorRejectsNullArguments()
    {
        var services = new ServiceCollection().BuildServiceProvider();

        Assert.Throws<ArgumentNullException>(() => new Mediator(null!, new SequentialPublisher()));
        Assert.Throws<ArgumentNullException>(() => new Mediator(services, null!));
    }
}
