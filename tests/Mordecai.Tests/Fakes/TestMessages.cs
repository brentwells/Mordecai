using System.Collections.Concurrent;

namespace Mordecai.Tests.Fakes;

/// <summary>A request with a handler registered in every test that uses it.</summary>
public sealed record Echo(string Message) : IRequest<string>;

public sealed class EchoHandler : IRequestHandler<Echo, string>
{
    public Task<string> Handle(Echo request, CancellationToken cancellationToken)
        => Task.FromResult($"echo:{request.Message}");
}

/// <summary>A request deliberately left without a handler.</summary>
public sealed record Unhandled : IRequest<string>;

/// <summary>Returns the token the mediator handed the handler, so a test can compare it.</summary>
public sealed record ProbeToken : IRequest<CancellationToken>;

public sealed class ProbeTokenHandler : IRequestHandler<ProbeToken, CancellationToken>
{
    public Task<CancellationToken> Handle(ProbeToken request, CancellationToken cancellationToken)
        => Task.FromResult(cancellationToken);
}

/// <summary>A request that returns nothing, exercising the <see cref="Unit"/> path.</summary>
public sealed record CancelOrder(int OrderId) : IRequest;

public sealed class CancelOrderHandler : IRequestHandler<CancelOrder>
{
    public List<int> Cancelled { get; } = [];

    public Task<Unit> Handle(CancelOrder request, CancellationToken cancellationToken)
    {
        Cancelled.Add(request.OrderId);
        return Unit.Task;
    }
}

/// <summary>Implements nothing; used to prove the weakly typed overloads validate their input.</summary>
public sealed class NotAMessage;

/// <summary>A notification with a variable number of handlers per test.</summary>
public sealed record Signal(string Name) : INotification;

/// <summary>Thread-safe ordered log, so the parallel publisher's tests are not racy.</summary>
public sealed class RecordingLog
{
    private readonly ConcurrentQueue<string> _entries = new();

    public void Add(string entry) => _entries.Enqueue(entry);

    public IReadOnlyList<string> Entries => [.. _entries];
}

public sealed class FirstSignalHandler(RecordingLog log) : INotificationHandler<Signal>
{
    public Task Handle(Signal notification, CancellationToken cancellationToken)
    {
        log.Add($"first:{notification.Name}");
        return Task.CompletedTask;
    }
}

public sealed class SecondSignalHandler(RecordingLog log) : INotificationHandler<Signal>
{
    public Task Handle(Signal notification, CancellationToken cancellationToken)
    {
        log.Add($"second:{notification.Name}");
        return Task.CompletedTask;
    }
}

/// <summary>A notification nothing subscribes to.</summary>
public sealed record Unheard : INotification;

/// <summary>
/// A notification reached through a base type, so the static type at the publish site differs
/// from the runtime type.
/// </summary>
public abstract record DomainEvent : INotification;

public sealed record OrderShipped(int OrderId) : DomainEvent;

public sealed class OrderShippedHandler(RecordingLog log) : INotificationHandler<OrderShipped>
{
    public Task Handle(OrderShipped notification, CancellationToken cancellationToken)
    {
        log.Add($"shipped:{notification.OrderId}");
        return Task.CompletedTask;
    }
}
