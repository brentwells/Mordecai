using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace Mordecai.Benchmarks;

/// <summary>
/// The benchmark that defines the project: cost of a single <c>Send</c> through a resolved
/// handler. MemoryDiagnoser is mandatory here — the allocation column is the number that must
/// stay at zero on the no-behavior path.
/// </summary>
/// <remarks>
/// Handlers are registered as singletons for every benchmark except
/// <see cref="SendWithTransientHandler"/>, so the allocation column shows what Mordecai itself
/// costs rather than what MS.DI charges to construct a handler. The transient case is measured
/// separately because it is the default, and the difference between the two is the honest
/// answer to "what does a send cost in my application".
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0)]
public class SendBenchmarks
{
    private static readonly GetOrderStatus Request = new(42);
    private static readonly OrderShipped Notification = new();

    private readonly List<IDisposable> _disposables = [];

    private GetOrderStatusHandler _handler = null!;
    private ISender _sender = null!;
    private ISender _senderWithBehaviors = null!;
    private ISender _transientSender = null!;
    private IPublisher _sequentialPublisher = null!;
    private IPublisher _parallelPublisher = null!;

    [GlobalSetup]
    public void Setup()
    {
        _handler = new GetOrderStatusHandler();

        _sender = Resolve<ISender>(static services =>
            services.AddMordecai(static cfg => cfg.Lifetime = ServiceLifetime.Singleton));

        _senderWithBehaviors = Resolve<ISender>(static services =>
            services.AddMordecai(static cfg =>
            {
                cfg.Lifetime = ServiceLifetime.Singleton;
                cfg.AddOpenBehavior(typeof(FirstPassThroughBehavior<,>));
                cfg.AddOpenBehavior(typeof(SecondPassThroughBehavior<,>));
                cfg.AddOpenBehavior(typeof(ThirdPassThroughBehavior<,>));
            }));

        _transientSender = Resolve<ISender>(
            static services => services.AddMordecai(static cfg => cfg.Lifetime = ServiceLifetime.Transient),
            ServiceLifetime.Transient);

        _sequentialPublisher = Resolve<IPublisher>(static services =>
            services.AddMordecai(static cfg =>
            {
                cfg.Lifetime = ServiceLifetime.Singleton;
                cfg.NotificationPublisher = new SequentialPublisher();
            }));

        _parallelPublisher = Resolve<IPublisher>(static services =>
            services.AddMordecai(static cfg =>
            {
                cfg.Lifetime = ServiceLifetime.Singleton;
                cfg.NotificationPublisher = new ParallelPublisher();
            }));

        // Warm the wrapper caches so the first measured iteration is not the one paying for
        // reflection. That cost is real but it is a startup cost, not a per-send cost.
        _sender.Send(Request).GetAwaiter().GetResult();
        _senderWithBehaviors.Send(Request).GetAwaiter().GetResult();
        _transientSender.Send(Request).GetAwaiter().GetResult();
        _sequentialPublisher.Publish(Notification).GetAwaiter().GetResult();
        _parallelPublisher.Publish(Notification).GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        _disposables.Clear();
    }

    [Benchmark(Baseline = true)]
    public Task<string> DirectHandlerCall() => _handler.Handle(Request, CancellationToken.None);

    [Benchmark]
    public Task<string> SendWithoutBehaviors() => _sender.Send(Request);

    [Benchmark]
    public Task<string> SendThroughThreeBehaviors() => _senderWithBehaviors.Send(Request);

    [Benchmark]
    public Task<string> SendWithTransientHandler() => _transientSender.Send(Request);

    [Benchmark]
    public Task PublishSequentialToThreeHandlers() => _sequentialPublisher.Publish(Notification);

    [Benchmark]
    public Task PublishParallelToThreeHandlers() => _parallelPublisher.Publish(Notification);

    private T Resolve<T>(
        Action<IServiceCollection> configure,
        ServiceLifetime handlerLifetime = ServiceLifetime.Singleton)
        where T : notnull
    {
        IServiceCollection services = new ServiceCollection();

        services.Add(new ServiceDescriptor(
            typeof(IRequestHandler<GetOrderStatus, string>), typeof(GetOrderStatusHandler), handlerLifetime));
        services.Add(new ServiceDescriptor(
            typeof(INotificationHandler<OrderShipped>), typeof(FirstOrderShippedHandler), handlerLifetime));
        services.Add(new ServiceDescriptor(
            typeof(INotificationHandler<OrderShipped>), typeof(SecondOrderShippedHandler), handlerLifetime));
        services.Add(new ServiceDescriptor(
            typeof(INotificationHandler<OrderShipped>), typeof(ThirdOrderShippedHandler), handlerLifetime));

        configure(services);

        var provider = services.BuildServiceProvider();
        _disposables.Add(provider);

        var scope = provider.CreateScope();
        _disposables.Add(scope);

        return scope.ServiceProvider.GetRequiredService<T>();
    }
}
