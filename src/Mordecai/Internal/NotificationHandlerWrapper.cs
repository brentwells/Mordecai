using Microsoft.Extensions.DependencyInjection;

namespace Mordecai.Internal;

/// <summary>
/// Resolves the handlers for a notification and hands them to the configured publisher.
/// </summary>
internal static class NotificationDispatcher
{
    public static Task Publish<TNotification>(
        TNotification notification,
        IServiceProvider services,
        INotificationPublisher publisher,
        CancellationToken cancellationToken)
        where TNotification : INotification
    {
        var handlers = services.GetServices<INotificationHandler<TNotification>>();
        var resolved = handlers as INotificationHandler<TNotification>[] ?? [.. handlers];

        if (resolved.Length == 0)
        {
            return Task.CompletedTask;
        }

        var executors = new NotificationHandlerExecutor[resolved.Length];

        for (var i = 0; i < resolved.Length; i++)
        {
            var handler = resolved[i];
            executors[i] = new NotificationHandlerExecutor(
                handler,
                (n, ct) => handler.Handle((TNotification)n, ct));
        }

        return publisher.Publish(executors, notification, cancellationToken);
    }
}

/// <summary>
/// Closes over the runtime notification type for the weakly typed <c>Publish(object)</c> overload.
/// The generic overload has a real type parameter and needs no wrapper.
/// </summary>
internal abstract class NotificationHandlerWrapper
{
    public abstract Task Handle(
        INotification notification,
        IServiceProvider services,
        INotificationPublisher publisher,
        CancellationToken cancellationToken);
}

/// <summary>The <typeparamref name="TNotification"/>-closed implementation.</summary>
/// <typeparam name="TNotification">The concrete notification type.</typeparam>
internal sealed class NotificationHandlerWrapperImpl<TNotification> : NotificationHandlerWrapper
    where TNotification : INotification
{
    public override Task Handle(
        INotification notification,
        IServiceProvider services,
        INotificationPublisher publisher,
        CancellationToken cancellationToken)
        => NotificationDispatcher.Publish((TNotification)notification, services, publisher, cancellationToken);
}
