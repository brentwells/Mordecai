namespace Mordecai.Tests.Fakes;

/// <summary>Marker used to prove a constrained behavior applies only to matching requests.</summary>
public interface IAudited;

public sealed record AuditedEcho(string Message) : IRequest<string>, IAudited;

public sealed class AuditedEchoHandler : IRequestHandler<AuditedEcho, string>
{
    public Task<string> Handle(AuditedEcho request, CancellationToken cancellationToken)
        => Task.FromResult($"echo:{request.Message}");
}

/// <summary>An <see cref="Echo"/> handler that records whether it ran at all.</summary>
public sealed class FlaggedEchoHandler : IRequestHandler<Echo, string>
{
    public bool Ran { get; private set; }

    public Task<string> Handle(Echo request, CancellationToken cancellationToken)
    {
        Ran = true;
        return Task.FromResult($"handled:{request.Message}");
    }
}

public sealed class OuterBehavior<TRequest, TResponse>(RecordingLog log)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        log.Add("outer:enter");
        var response = await next(cancellationToken).ConfigureAwait(false);
        log.Add("outer:exit");

        return response;
    }
}

public sealed class InnerBehavior<TRequest, TResponse>(RecordingLog log)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        log.Add("inner:enter");
        var response = await next(cancellationToken).ConfigureAwait(false);
        log.Add("inner:exit");

        return response;
    }
}

/// <summary>Applies only to requests carrying the <see cref="IAudited"/> marker.</summary>
public sealed class AuditBehavior<TRequest, TResponse>(RecordingLog log)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IAudited
{
    public Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        log.Add($"audit:{typeof(TRequest).Name}");

        return next(cancellationToken);
    }
}

/// <summary>Records that the handler threw, then lets the exception continue outwards.</summary>
public sealed class ObservingBehavior<TRequest, TResponse>(RecordingLog log)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        try
        {
            return await next(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            log.Add($"caught:{exception.Message}");
            throw;
        }
    }
}

/// <summary>Never calls <c>next</c>, so the handler must not run.</summary>
public sealed class ShortCircuitBehavior : IPipelineBehavior<Echo, string>
{
    public Task<string> Handle(
        Echo request,
        RequestHandlerDelegate<string> next,
        CancellationToken cancellationToken)
        => Task.FromResult("short-circuited");
}

/// <summary>Replaces whatever the rest of the pipeline produced.</summary>
public sealed class RewritingBehavior : IPipelineBehavior<Echo, string>
{
    public async Task<string> Handle(
        Echo request,
        RequestHandlerDelegate<string> next,
        CancellationToken cancellationToken)
        => $"[{await next(cancellationToken).ConfigureAwait(false)}]";
}

/// <summary>A closed behavior, for the test that mixes closed and open registrations.</summary>
public sealed class ClosedEchoBehavior(RecordingLog log) : IPipelineBehavior<Echo, string>
{
    public async Task<string> Handle(
        Echo request,
        RequestHandlerDelegate<string> next,
        CancellationToken cancellationToken)
    {
        log.Add("closed:enter");
        var response = await next(cancellationToken).ConfigureAwait(false);
        log.Add("closed:exit");

        return response;
    }
}

/// <summary>Swaps in a different token, to prove the substituted one reaches the handler.</summary>
public sealed class TokenSubstitutingBehavior : IPipelineBehavior<ProbeToken, CancellationToken>, IDisposable
{
    private readonly CancellationTokenSource _source = new();

    public CancellationToken SubstitutedToken => _source.Token;

    public Task<CancellationToken> Handle(
        ProbeToken request,
        RequestHandlerDelegate<CancellationToken> next,
        CancellationToken cancellationToken)
        => next(_source.Token);

    public void Dispose() => _source.Dispose();
}

/// <summary>A handler that always throws, for the exception-propagation test.</summary>
public sealed record Explode : IRequest<string>;

public sealed class ExplodeHandler : IRequestHandler<Explode, string>
{
    public Task<string> Handle(Explode request, CancellationToken cancellationToken)
        => throw new InvalidOperationException("handler-failed");
}

/// <summary>
/// A generic marker of the shape a CQRS split produces. The behavior constrained to it references
/// both type parameters, which is a harder case for the container's constraint filtering than a
/// plain marker interface.
/// </summary>
/// <typeparam name="TResponse">The response type.</typeparam>
public interface IQueryRequest<out TResponse> : IRequest<TResponse>;

public sealed record QueryEcho(string Message) : IQueryRequest<string>;

public sealed class QueryEchoHandler : IRequestHandler<QueryEcho, string>
{
    public Task<string> Handle(QueryEcho request, CancellationToken cancellationToken)
        => Task.FromResult($"query:{request.Message}");
}

public sealed class QueryOnlyBehavior<TRequest, TResponse>(RecordingLog log)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IQueryRequest<TResponse>
{
    public Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        log.Add($"query-only:{typeof(TRequest).Name}");

        return next(cancellationToken);
    }
}

/// <summary>Fails its first two calls, so a retrying behavior has something to retry.</summary>
public sealed record Flaky : IRequest<string>;

public sealed class FlakyHandler : IRequestHandler<Flaky, string>
{
    public int Calls { get; private set; }

    public Task<string> Handle(Flaky request, CancellationToken cancellationToken)
    {
        Calls++;

        return Calls < 3
            ? throw new InvalidOperationException($"transient-{Calls}")
            : Task.FromResult($"ok-on-{Calls}");
    }
}

public sealed class RetryingBehavior<TRequest, TResponse>(RecordingLog log)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await next(cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException) when (attempt < 3)
            {
                log.Add($"retry:{attempt}");
            }
        }
    }
}

/// <summary>
/// The static-abstract factory that lets an open-generic behavior short-circuit: without it there
/// is no way to produce a <c>TResponse</c> the behavior did not receive.
/// </summary>
/// <typeparam name="TSelf">The implementing type.</typeparam>
public interface IOutcome<TSelf>
    where TSelf : IOutcome<TSelf>
{
    static abstract TSelf FromError(string error);
}

public sealed record Outcome(string? Value, string? Error) : IOutcome<Outcome>
{
    public static Outcome FromError(string error) => new(null, error);
}

public sealed record RiskyOperation : IRequest<Outcome>;

public sealed class RiskyOperationHandler : IRequestHandler<RiskyOperation, Outcome>
{
    public Task<Outcome> Handle(RiskyOperation request, CancellationToken cancellationToken)
        => throw new InvalidOperationException("domain-failure");
}

public sealed class OutcomeMappingBehavior<TRequest, TResponse>(RecordingLog log)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
    where TResponse : IOutcome<TResponse>
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        log.Add($"mapping:{typeof(TRequest).Name}");

        try
        {
            return await next(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            return TResponse.FromError(exception.Message);
        }
    }
}
