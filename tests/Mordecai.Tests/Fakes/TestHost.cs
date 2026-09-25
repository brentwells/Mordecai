using Microsoft.Extensions.DependencyInjection;

namespace Mordecai.Tests.Fakes;

/// <summary>
/// Builds a container and a mediator over it. Registration is manual here on purpose — several
/// of these tests predate scanning and must keep working without it.
/// </summary>
public sealed class TestHost : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    private TestHost(ServiceProvider provider, IMediator mediator)
    {
        _provider = provider;
        Mediator = mediator;
    }

    public IMediator Mediator { get; }

    public IServiceProvider Services => _provider;

    public static TestHost Build(
        Action<IServiceCollection> configure,
        INotificationPublisher? notificationPublisher = null)
    {
        var services = new ServiceCollection();
        configure(services);

        var provider = services.BuildServiceProvider();

        return new TestHost(provider, new Mediator(provider, notificationPublisher ?? new SequentialPublisher()));
    }

    public ValueTask DisposeAsync() => _provider.DisposeAsync();
}
