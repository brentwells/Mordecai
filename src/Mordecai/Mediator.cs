using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Mordecai.Internal;

namespace Mordecai;

/// <summary>
/// The default <see cref="IMediator"/>. Routes a request to its single handler and broadcasts a
/// notification to every registered handler, resolving both from the supplied
/// <see cref="IServiceProvider"/>.
/// </summary>
/// <remarks>
/// Resolve this through the container rather than constructing it. <c>AddMordecai</c> registers
/// <see cref="IMediator"/>, <see cref="ISender"/>, and <see cref="IPublisher"/> so all three
/// resolve to the same instance within a scope.
/// </remarks>
public sealed class Mediator : IMediator
{
    private readonly IServiceProvider _services;
    private readonly INotificationPublisher _notificationPublisher;
    private readonly HandlerWrapperCache _cache;

    /// <summary>Creates a mediator over the given provider and publisher strategy.</summary>
    /// <param name="services">The provider handlers and behaviors are resolved from.</param>
    /// <param name="notificationPublisher">The strategy used to invoke notification handlers.</param>
    public Mediator(IServiceProvider services, INotificationPublisher notificationPublisher)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(notificationPublisher);

        _services = services;
        _notificationPublisher = notificationPublisher;

        // AddMordecai registers the cache as a singleton so every scope shares one. A mediator
        // built by hand — in a test, say — gets a private cache instead of leaking wrappers that
        // memoise another container's registrations.
        _cache = services.GetService<HandlerWrapperCache>() ?? new HandlerWrapperCache();
    }

    /// <inheritdoc />
    public Task<TResponse> Send<TResponse>(
        IRequest<TResponse> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var wrapper = _cache.GetRequestWrapper<TResponse>(request.GetType());

        return wrapper.Handle(request, _services, cancellationToken);
    }

    /// <inheritdoc />
    [RequiresUnreferencedCode("Weakly typed dispatch inspects the request type with reflection.")]
    [RequiresDynamicCode("Weakly typed dispatch constructs generic types at runtime.")]
    public Task<object?> Send(object request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request is not IBaseRequest)
        {
            throw new ArgumentException(
                $"'{request.GetType()}' does not implement {nameof(IBaseRequest)}, so it is not a request.",
                nameof(request));
        }

        var wrapper = _cache.GetObjectRequestWrapper(request.GetType());

        return wrapper.Handle(request, _services, cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
        IStreamRequest<TResponse> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var wrapper = _cache.GetStreamWrapper<TResponse>(request.GetType());

        return wrapper.Handle(request, _services, cancellationToken);
    }

    /// <inheritdoc />
    [RequiresUnreferencedCode("Weakly typed dispatch inspects the request type with reflection.")]
    [RequiresDynamicCode("Weakly typed dispatch constructs generic types at runtime.")]
    public IAsyncEnumerable<object?> CreateStream(
        object request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // There is no non-generic marker for a stream request, so the check is "does it implement
        // IStreamRequest<> at all", which the cache answers while resolving the response type.
        var wrapper = _cache.GetObjectStreamWrapper(request.GetType());

        return wrapper.Handle(request, _services, cancellationToken);
    }

    /// <inheritdoc />
    public Task Publish<TNotification>(
        TNotification notification,
        CancellationToken cancellationToken = default)
        where TNotification : INotification
    {
        ArgumentNullException.ThrowIfNull(notification);

        // TNotification is inferred from the *static* type of the argument, which is not always
        // the runtime type: `INotification evt = new OrderPlaced(...); Publish(evt)` infers
        // INotification and would resolve INotificationHandler<INotification>, find nothing, and
        // deliver to no one -- silently, because zero handlers is a legal outcome. When the two
        // disagree, fall through to the wrapper keyed on the runtime type.
        if (typeof(TNotification) != notification.GetType())
        {
            return _cache
                .GetNotificationWrapper(notification.GetType())
                .Handle(notification, _services, _notificationPublisher, cancellationToken);
        }

        return NotificationDispatcher.Publish(
            notification, _services, _notificationPublisher, cancellationToken);
    }

    /// <inheritdoc />
    [RequiresUnreferencedCode("Weakly typed publish inspects the notification type with reflection.")]
    [RequiresDynamicCode("Weakly typed publish constructs generic types at runtime.")]
    public Task Publish(object notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (notification is not INotification typed)
        {
            throw new ArgumentException(
                $"'{notification.GetType()}' does not implement {nameof(INotification)}.",
                nameof(notification));
        }

        var wrapper = _cache.GetNotificationWrapper(typed.GetType());

        return wrapper.Handle(typed, _services, _notificationPublisher, cancellationToken);
    }
}
