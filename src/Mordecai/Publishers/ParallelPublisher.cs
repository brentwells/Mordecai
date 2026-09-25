namespace Mordecai;

/// <summary>
/// Starts every notification handler together and waits for all of them. A handler that throws
/// does not stop the others; once all have finished, the failures surface together as an
/// <see cref="AggregateException"/>.
/// </summary>
/// <remarks>
/// Handlers must be thread-safe and must not share a scoped service that is not. A scoped
/// <c>DbContext</c> touched by two handlers under this publisher fails at runtime; keep
/// <see cref="SequentialPublisher"/>, or give each handler its own scope.
/// </remarks>
public sealed class ParallelPublisher : INotificationPublisher
{
    /// <inheritdoc />
    public async Task Publish(
        IEnumerable<NotificationHandlerExecutor> handlerExecutors,
        INotification notification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handlerExecutors);

        List<Task> running = [];

        foreach (var executor in handlerExecutors)
        {
            running.Add(Invoke(executor, notification, cancellationToken));
        }

        var all = Task.WhenAll(running);

        try
        {
            await all.ConfigureAwait(false);
        }
        catch (Exception) when (all.Exception is not null)
        {
            // Awaiting Task.WhenAll rethrows only the first fault. The contract here is that
            // every failure is reported, so throw the aggregate the task itself carries.
            throw all.Exception;
        }
    }

    // An async wrapper so a handler that throws synchronously — before it ever returns a task —
    // produces a faulted task instead of aborting the loop and starving the handlers after it.
    private static async Task Invoke(
        NotificationHandlerExecutor executor,
        INotification notification,
        CancellationToken cancellationToken)
        => await executor.HandlerCallback(notification, cancellationToken).ConfigureAwait(false);
}
