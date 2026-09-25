namespace Mordecai;

/// <summary>
/// Invokes notification handlers one at a time, awaiting each before starting the next. The
/// default: predictable, debuggable, and safe to use with a shared scoped service such as a
/// <c>DbContext</c>. The first handler to throw stops the remaining handlers and the exception
/// surfaces directly.
/// </summary>
public sealed class SequentialPublisher : INotificationPublisher
{
    /// <inheritdoc />
    public async Task Publish(
        IEnumerable<NotificationHandlerExecutor> handlerExecutors,
        INotification notification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handlerExecutors);

        foreach (var executor in handlerExecutors)
        {
            await executor.HandlerCallback(notification, cancellationToken).ConfigureAwait(false);
        }
    }
}
