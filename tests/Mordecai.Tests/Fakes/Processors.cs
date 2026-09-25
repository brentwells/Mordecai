namespace Mordecai.Tests.Fakes;

public sealed class FirstPreProcessor<TRequest>(RecordingLog log) : IRequestPreProcessor<TRequest>
    where TRequest : notnull
{
    public Task Process(TRequest request, CancellationToken cancellationToken)
    {
        log.Add($"pre-1:{typeof(TRequest).Name}");

        return Task.CompletedTask;
    }
}

public sealed class SecondPreProcessor<TRequest>(RecordingLog log) : IRequestPreProcessor<TRequest>
    where TRequest : notnull
{
    public Task Process(TRequest request, CancellationToken cancellationToken)
    {
        log.Add($"pre-2:{typeof(TRequest).Name}");

        return Task.CompletedTask;
    }
}

public sealed class ThrowingPreProcessor<TRequest> : IRequestPreProcessor<TRequest>
    where TRequest : notnull
{
    public Task Process(TRequest request, CancellationToken cancellationToken)
        => throw new InvalidOperationException("pre-failed");
}

public sealed class RecordingPostProcessor<TRequest, TResponse>(RecordingLog log)
    : IRequestPostProcessor<TRequest, TResponse>
    where TRequest : notnull
{
    public Task Process(TRequest request, TResponse response, CancellationToken cancellationToken)
    {
        log.Add($"post:{response}");

        return Task.CompletedTask;
    }
}
