namespace Mordecai;

/// <summary>Handles <typeparamref name="TRequest"/> and returns <typeparamref name="TResponse"/>.</summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
public interface IRequestHandler<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    /// <summary>Handles the request.</summary>
    /// <param name="request">The request instance.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The response.</returns>
    Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}

/// <summary>Handles a <see cref="IRequest"/> that returns no value.</summary>
/// <typeparam name="TRequest">The request type.</typeparam>
public interface IRequestHandler<in TRequest> : IRequestHandler<TRequest, Unit>
    where TRequest : IRequest<Unit>;

/// <summary>Handles <typeparamref name="TRequest"/> by producing a stream of responses.</summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The element type of the stream.</typeparam>
public interface IStreamRequestHandler<in TRequest, out TResponse>
    where TRequest : IStreamRequest<TResponse>
{
    /// <summary>Handles the request, yielding responses as they become available.</summary>
    /// <param name="request">The request instance.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The response stream.</returns>
    IAsyncEnumerable<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}

/// <summary>Handles a published <typeparamref name="TNotification"/>.</summary>
/// <typeparam name="TNotification">The notification type.</typeparam>
public interface INotificationHandler<in TNotification>
    where TNotification : INotification
{
    /// <summary>Handles the notification.</summary>
    /// <param name="notification">The notification instance.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    Task Handle(TNotification notification, CancellationToken cancellationToken);
}
