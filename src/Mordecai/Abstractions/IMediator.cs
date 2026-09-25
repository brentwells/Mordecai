namespace Mordecai;

/// <summary>Sends a request to its single handler.</summary>
public interface ISender
{
    /// <summary>Sends a request and awaits its response.</summary>
    /// <typeparam name="TResponse">The response type.</typeparam>
    /// <param name="request">The request instance.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The handler's response.</returns>
    Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Weakly-typed send. Present for compatibility; the response type is not known at
    /// compile time, so this path cannot use generated dispatch.
    /// </summary>
    /// <param name="request">The request instance.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The handler's response, boxed.</returns>
    Task<object?> Send(object request, CancellationToken cancellationToken = default);

    /// <summary>Sends a stream request and returns its response stream.</summary>
    /// <typeparam name="TResponse">The element type of the stream.</typeparam>
    /// <param name="request">The request instance.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The response stream.</returns>
    IAsyncEnumerable<TResponse> CreateStream<TResponse>(
        IStreamRequest<TResponse> request,
        CancellationToken cancellationToken = default);

    /// <summary>Weakly-typed stream send.</summary>
    /// <param name="request">The request instance.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The response stream, boxed.</returns>
    IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default);
}

/// <summary>Publishes a notification to all registered handlers.</summary>
public interface IPublisher
{
    /// <summary>Weakly-typed publish.</summary>
    /// <param name="notification">The notification instance.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    Task Publish(object notification, CancellationToken cancellationToken = default);

    /// <summary>Publishes a notification to every registered handler.</summary>
    /// <typeparam name="TNotification">The notification type.</typeparam>
    /// <param name="notification">The notification instance.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification;
}

/// <summary>The full mediator surface.</summary>
public interface IMediator : ISender, IPublisher;

/// <summary>
/// Strategy controlling how notification handlers are invoked. Swap this to choose
/// between sequential and concurrent delivery.
/// </summary>
public interface INotificationPublisher
{
    /// <summary>Invokes each resolved handler according to this strategy.</summary>
    /// <param name="handlerExecutors">The resolved handlers.</param>
    /// <param name="notification">The notification being delivered.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    Task Publish(
        IEnumerable<NotificationHandlerExecutor> handlerExecutors,
        INotification notification,
        CancellationToken cancellationToken);
}

/// <summary>A resolved notification handler paired with its invocation callback.</summary>
/// <param name="HandlerInstance">The handler resolved from the container.</param>
/// <param name="HandlerCallback">Invokes the handler with a correctly typed notification.</param>
public readonly record struct NotificationHandlerExecutor(
    object HandlerInstance,
    Func<INotification, CancellationToken, Task> HandlerCallback);
