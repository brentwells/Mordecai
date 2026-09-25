using System.Runtime.CompilerServices;

namespace Mordecai.Tests.Fakes;

public sealed record Countdown(int Count) : IStreamRequest<int>;

public sealed class CountdownHandler : IStreamRequestHandler<Countdown, int>
{
    /// <summary>The token the handler was actually given, so a test can compare it.</summary>
    public CancellationToken ObservedToken { get; private set; }

    /// <summary>Set when the iterator is disposed, whether it ran to completion or not.</summary>
    public bool Disposed { get; private set; }

    /// <summary>Set only when the loop ran out naturally.</summary>
    public bool RanToCompletion { get; private set; }

    public async IAsyncEnumerable<int> Handle(
        Countdown request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObservedToken = cancellationToken;

        try
        {
            for (var i = 0; i < request.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();

                yield return i;
            }

            RanToCompletion = true;
        }
        finally
        {
            Disposed = true;
        }
    }
}

/// <summary>A stream request deliberately left without a handler.</summary>
public sealed record UnhandledStream : IStreamRequest<int>;

/// <summary>Logs every element on its way past, and logs again once the stream ends.</summary>
public sealed class RecordingStreamBehavior<TRequest, TResponse>(RecordingLog log)
    : IStreamPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async IAsyncEnumerable<TResponse> Handle(
        TRequest request,
        StreamHandlerDelegate<TResponse> next,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        log.Add("stream:enter");

        await foreach (var item in next(cancellationToken).ConfigureAwait(false).WithCancellation(cancellationToken))
        {
            log.Add($"stream:item:{item}");

            yield return item;
        }

        // Only reached if the consumer enumerated to the end; breaking out early disposes the
        // iterator here instead.
        log.Add("stream:exit");
    }
}
