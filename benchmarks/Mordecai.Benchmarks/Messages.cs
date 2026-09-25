namespace Mordecai.Benchmarks;

public sealed record GetOrderStatus(int OrderId) : IRequest<string>;

/// <summary>
/// Returns a cached task on purpose. A handler doing real work would dominate the measurement,
/// and <c>Task.FromResult</c> would put its own allocation in the column that matters.
/// </summary>
public sealed class GetOrderStatusHandler : IRequestHandler<GetOrderStatus, string>
{
    private static readonly Task<string> Shipped = Task.FromResult("shipped");

    public Task<string> Handle(GetOrderStatus request, CancellationToken cancellationToken) => Shipped;
}

public sealed record OrderShipped : INotification;

public sealed class FirstOrderShippedHandler : INotificationHandler<OrderShipped>
{
    public Task Handle(OrderShipped notification, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class SecondOrderShippedHandler : INotificationHandler<OrderShipped>
{
    public Task Handle(OrderShipped notification, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class ThirdOrderShippedHandler : INotificationHandler<OrderShipped>
{
    public Task Handle(OrderShipped notification, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class FirstPassThroughBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
        => next(cancellationToken);
}

public sealed class SecondPassThroughBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
        => next(cancellationToken);
}

public sealed class ThirdPassThroughBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
        => next(cancellationToken);
}
