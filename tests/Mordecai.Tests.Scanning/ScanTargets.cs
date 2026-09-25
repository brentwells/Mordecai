using System.Runtime.CompilerServices;

namespace Mordecai.Tests.Scanning;

/// <summary>Somewhere for a discovered handler to record that it ran.</summary>
public interface IScanSink
{
    void Record(string entry);
}

/// <summary>Marker used by <c>RegisterServicesFromAssemblyContaining</c> tests.</summary>
public sealed class ScanTargetMarker;

/// <summary>A second marker in the same assembly, for the deduplication tests.</summary>
public sealed class OtherScanTargetMarker;

public sealed record Ordered(int Id) : IRequest<string>;

public sealed class OrderedHandler(IScanSink sink) : IRequestHandler<Ordered, string>
{
    public Task<string> Handle(Ordered request, CancellationToken cancellationToken)
    {
        sink.Record($"ordered:{request.Id}");
        return Task.FromResult($"ordered:{request.Id}");
    }
}

public sealed record VoidJob(string Name) : IRequest;

public sealed class VoidJobHandler(IScanSink sink) : IRequestHandler<VoidJob>
{
    public Task<Unit> Handle(VoidJob request, CancellationToken cancellationToken)
    {
        sink.Record($"job:{request.Name}");
        return Unit.Task;
    }
}

public sealed record Ticker(int Count) : IStreamRequest<int>;

public sealed class TickerHandler : IStreamRequestHandler<Ticker, int>
{
    public async IAsyncEnumerable<int> Handle(
        Ticker request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; i < request.Count; i++)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return i;
        }
    }
}

public sealed record Beeped(string Source) : INotification;

public sealed class FirstBeepHandler(IScanSink sink) : INotificationHandler<Beeped>
{
    public Task Handle(Beeped notification, CancellationToken cancellationToken)
    {
        sink.Record($"beep-1:{notification.Source}");
        return Task.CompletedTask;
    }
}

public sealed class SecondBeepHandler(IScanSink sink) : INotificationHandler<Beeped>
{
    public Task Handle(Beeped notification, CancellationToken cancellationToken)
    {
        sink.Record($"beep-2:{notification.Source}");
        return Task.CompletedTask;
    }
}

/// <summary>Abstract handlers must be skipped, not registered.</summary>
public abstract class AbstractOrderedHandler : IRequestHandler<Ordered, string>
{
    public abstract Task<string> Handle(Ordered request, CancellationToken cancellationToken);
}
