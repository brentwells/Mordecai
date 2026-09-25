# The Complete Behavior Catalogue

Every behavior we know how to build with `IPipelineBehavior<TRequest, TResponse>` and
`IStreamPipelineBehavior<TRequest, TResponse>`, each with working code.

This page is a parts bin, not a recommended stack. It takes no position on what you should run,
in what order, or whether you should run anything at all — composition, ordering, and the
adjacencies that matter are [the behaviors guide's](behaviors.md#2-a-stack-that-works) business.
Here there are only possibilities.

Every example compiles against the real abstractions. Marker interfaces are defined inline where
they are introduced; infrastructure services (`ICache`, `IAuditLog`, `IRateLimiter`, …) are your
application's own abstractions — any implementation works behind them.

---

## At a glance

| Behavior | Lever | Applies to |
| --- | --- | --- |
| [Logging](#logging) | Wrap | every request |
| [Payload logging](#payload-logging) | Before | `ILogSafe` |
| [Metrics](#metrics) | Wrap | every request |
| [Tracing](#tracing) | Wrap | every request |
| [Slow-request watchdog](#slow-request-watchdog) | Wrap | every request |
| [Audit trail](#audit-trail) | After | `IAudited` |
| [Validation](#validation) | Before | every request; validators opt in |
| [Role authorization](#role-authorization) | Before | `IRequireRole` |
| [Policy authorization](#policy-authorization) | Before | `IPolicyProtected` |
| [Tenant guard](#tenant-guard) | Before | `ITenantOwned` |
| [Feature gate](#feature-gate) | Skip (throw) | `IFeatureGated` |
| [Maintenance gate](#maintenance-gate) | Skip (throw) | `IWriteOperation` |
| [Rate limiting](#rate-limiting) | Skip (throw) | `IRateLimited` |
| [Concurrency throttle](#concurrency-throttle) | Wrap | `IThrottled` |
| [Read-through cache](#read-through-cache) | Skip | `ICacheable` |
| [Cache eviction](#cache-eviction) | After | `ICacheEvicting` |
| [Request coalescing](#request-coalescing) | Skip | `ICoalescable` |
| [Idempotency](#idempotency) | Skip | `IIdempotent` |
| [Timeout](#timeout) | Token | `IHasTimeout` |
| [Retry](#retry) | Repeat | `IRetryable` |
| [Conflict retry](#conflict-retry) | Repeat | `IRetryOnConflict` |
| [Circuit breaker](#circuit-breaker) | Skip + wrap | `ICircuitProtected` |
| [Fallback](#fallback) | Wrap | `TResponse : IHasFallback<TResponse>` |
| [Hedging](#hedging) | Repeat + token | `IHedged` |
| [Transaction](#transaction) | Wrap | `ITransactional` |
| [Unit-of-work save](#unit-of-work-save) | After | `IMutating` |
| [Outbox staging](#outbox-staging) | After | every request |
| [Domain event dispatch](#domain-event-dispatch) | After | every request |
| [Distributed lock](#distributed-lock) | Wrap | `ILockable` |
| [Correlation](#correlation) | Wrap | every request |
| [Culture pinning](#culture-pinning) | Wrap | `ICultureSensitive` |
| [Request stamping](#request-stamping) | Before | `IStamped` |
| [Input normalisation](#input-normalisation) | Before | `IPagedRequest` |
| [Exception mapping](#exception-mapping) | Wrap | `TResponse : IResult<TResponse>` |
| [Response redaction](#response-redaction) | After | `TResponse : IRedactable<TResponse>` |
| [Chaos injection](#chaos-injection) | Before | every request, when enabled |
| [Shadowing](#shadowing) | After | `IShadowed<TResponse>` |
| [Deferred execution](#deferred-execution) | Skip | `IDeferred` |
| [Stream timing](#stream-timing) | Wrap | every stream |
| [Stall watchdog](#stall-watchdog) | Token | `IGapLimited` |
| [Pacing](#pacing) | After each element | `IPaced` |
| [Sampling](#sampling) | Skip elements | `ISampled` |
| [Chunked release](#chunked-release) | Buffer | `IChunkReleased` |

---

## 1. Reading this page

The **Lever** column uses [the six levers](behaviors.md#1-the-six-levers): run *before* `next`,
run *after* it, *skip* it, *wrap* it, substitute the *token*, or call it *repeatedly*. Stream
entries stretch the vocabulary slightly, since a stream behavior sits inside the enumeration
rather than around a single awaited call.

Registration is the same for everything here unless an entry says otherwise:

```csharp
cfg.AddOpenBehavior(typeof(LoggingBehavior<,>));
cfg.AddOpenStreamBehavior(typeof(StreamTimingBehavior<,>));
```

Two recurring implementation notes, called out once so the entries can reference them:

- **A static field in a generic type is one field per closed type.** For a behavior that wants
  per-request-type state — a coalescing map, a semaphore, a circuit — that is a feature: the
  partitioning comes free. For state that should be global — a `Meter`, an `ActivitySource` — it
  is a bug, and the static belongs in a non-generic holder class instead.
- **An open-generic singleton is one instance per closed type**, not one instance overall. A
  singleton behavior with instance fields therefore also gets per-request-type state, with the
  container rather than the CLR doing the partitioning.

---

## 2. Observation

Behaviors that watch and never interfere.

### Logging

One log line in, one out, and failures logged with the exception attached.

```csharp
public sealed class LoggingBehavior<TRequest, TResponse>(
    ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Handling {Request}", typeof(TRequest).Name);

        try
        {
            var response = await next(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("{Request} succeeded", typeof(TRequest).Name);

            return response;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "{Request} failed", typeof(TRequest).Name);
            throw;
        }
    }
}
```

### Payload logging

Logging the request *contents* is a data-leak generator unless the request controls its own
representation. Constrain on a marker whose one job is producing a safe rendering:

```csharp
public interface ILogSafe
{
    string ToLogString();
}

public sealed record ChargeCard(string CardNumber, decimal Amount) : IRequest, ILogSafe
{
    public string ToLogString() => $"ChargeCard(****{CardNumber[^4..]}, {Amount})";
}

public sealed class PayloadLoggingBehavior<TRequest, TResponse>(
    ILogger<PayloadLoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : ILogSafe
{
    public Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Handling {Request}: {Payload}", typeof(TRequest).Name, request.ToLogString());

        return next(cancellationToken);
    }
}
```

A request that has not thought about what is safe to log does not get its payload logged. That is
the point of the constraint.

### Metrics

A duration histogram tagged by request type and outcome, using `System.Diagnostics.Metrics`
directly. The instruments live in a non-generic holder — one histogram for the process, not one
per closed type.

```csharp
internal static class MediatorMetrics
{
    internal static readonly Histogram<double> Duration = new Meter("MyApp.Mediator")
        .CreateHistogram<double>("mediator.send.duration", unit: "ms");
}

public sealed class MetricsBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        var outcome = "ok";

        try
        {
            return await next(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            outcome = "error";
            throw;
        }
        finally
        {
            MediatorMetrics.Duration.Record(
                Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                new KeyValuePair<string, object?>("request", typeof(TRequest).Name),
                new KeyValuePair<string, object?>("outcome", outcome));
        }
    }
}
```

The `finally` is what guarantees an exception cannot lose the measurement.

### Tracing

One span per send, tagged with the request type, with the failure recorded on the span:

```csharp
internal static class MediatorTracing
{
    internal static readonly ActivitySource Source = new("MyApp.Mediator");
}

public sealed class TracingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        using var activity = MediatorTracing.Source.StartActivity($"Send {typeof(TRequest).Name}");
        activity?.SetTag("mediator.request", typeof(TRequest).FullName);

        try
        {
            var response = await next(cancellationToken).ConfigureAwait(false);
            activity?.SetStatus(ActivityStatusCode.Ok);

            return response;
        }
        catch (Exception exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            activity?.AddException(exception);
            throw;
        }
    }
}
```

### Slow-request watchdog

Silence for the fast path, a warning for anything over threshold. Cheaper to keep enabled
everywhere than full request logging, and it pays for itself the first time production slows down.

```csharp
public sealed class SlowRequestBehavior<TRequest, TResponse>(
    ILogger<SlowRequestBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private static readonly TimeSpan Threshold = TimeSpan.FromMilliseconds(500);

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();

        try
        {
            return await next(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(start);

            if (elapsed > Threshold)
            {
                logger.LogWarning("{Request} took {Elapsed}", typeof(TRequest).Name, elapsed);
            }
        }
    }
}
```

### Audit trail

Who did what, when — written after the handler succeeds, so the trail records things that
actually happened. Move the write into a `finally` and record the outcome if failed attempts
must be auditable too.

```csharp
public interface IAudited;

public sealed class AuditBehavior<TRequest, TResponse>(IAuditLog audit, ICurrentUser user)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IAudited
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var response = await next(cancellationToken).ConfigureAwait(false);

        await audit
            .WriteAsync(user.UserName, typeof(TRequest).Name, DateTimeOffset.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        return response;
    }
}
```

---

## 3. Admission control

Behaviors that decide whether the handler runs at all.

### Validation

Collects every validator registered for the request type; a request with no validators passes
straight through, so the behavior can safely apply to everything.

```csharp
public sealed class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        List<string> errors = [];

        foreach (var validator in validators)
        {
            var result = await validator.ValidateAsync(request, cancellationToken).ConfigureAwait(false);

            if (!result.IsValid)
            {
                errors.AddRange(result.Errors);
            }
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        return await next(cancellationToken).ConfigureAwait(false);
    }
}
```

### Role authorization

The request declares what it demands; the behavior checks the caller against it. Throwing,
because an open behavior cannot fabricate a `TResponse` — see
[the short-circuit problem](behaviors.md#4-the-short-circuit-problem).

```csharp
public interface IRequireRole
{
    string RequiredRole { get; }
}

public sealed class RoleAuthorizationBehavior<TRequest, TResponse>(ICurrentUser user)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequireRole
{
    public Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (!user.IsInRole(request.RequiredRole))
        {
            throw new UnauthorizedAccessException(
                $"'{typeof(TRequest).Name}' requires role '{request.RequiredRole}'.");
        }

        return next(cancellationToken);
    }
}
```

### Policy authorization

When a role check is not enough — the decision depends on the resource being touched — hand the
whole request to an authorization service as the resource:

```csharp
public interface IPolicyProtected
{
    string Policy { get; }
}

public sealed class PolicyAuthorizationBehavior<TRequest, TResponse>(IAuthorizationService authorization)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IPolicyProtected
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var authorized = await authorization
            .AuthorizeAsync(request, request.Policy, cancellationToken)
            .ConfigureAwait(false);

        if (!authorized)
        {
            throw new UnauthorizedAccessException(
                $"'{typeof(TRequest).Name}' failed policy '{request.Policy}'.");
        }

        return await next(cancellationToken).ConfigureAwait(false);
    }
}
```

### Tenant guard

In a multi-tenant system, the one check you never want to forget on any handler is the one a
behavior can make unforgettable: the request's tenant must be the caller's tenant.

```csharp
public interface ITenantOwned
{
    Guid TenantId { get; }
}

public sealed class TenantGuardBehavior<TRequest, TResponse>(ITenantContext tenant)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : ITenantOwned
{
    public Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (request.TenantId != tenant.TenantId)
        {
            throw new UnauthorizedAccessException(
                $"'{typeof(TRequest).Name}' targets tenant '{request.TenantId}', " +
                $"but the caller is scoped to '{tenant.TenantId}'.");
        }

        return next(cancellationToken);
    }
}
```

### Feature gate

A kill switch per request type, controlled from configuration rather than a deploy:

```csharp
public interface IFeatureGated
{
    string FeatureName { get; }
}

public sealed class FeatureDisabledException(string feature)
    : Exception($"Feature '{feature}' is disabled.");

public sealed class FeatureGateBehavior<TRequest, TResponse>(IFeatureFlags flags)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IFeatureGated
{
    public Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (!flags.IsEnabled(request.FeatureName))
        {
            throw new FeatureDisabledException(request.FeatureName);
        }

        return next(cancellationToken);
    }
}
```

### Maintenance gate

Pause writes, keep serving reads. The marker does the split: only requests declaring themselves
as writes are gated, and everything else never sees the behavior.

```csharp
public interface IWriteOperation;

public sealed class MaintenanceModeException()
    : Exception("The system is in maintenance mode; writes are paused.");

public sealed class MaintenanceGateBehavior<TRequest, TResponse>(IOperationalState state)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IWriteOperation
{
    public Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (state.WritesArePaused)
        {
            throw new MaintenanceModeException();
        }

        return next(cancellationToken);
    }
}
```

### Rate limiting

The request names its own partition key — per user, per API key, per tenant — and the limiter
decides. Rejected requests never reach the handler.

```csharp
public interface IRateLimited
{
    string RateLimitKey { get; }
}

public sealed class RateLimitExceededException(string key)
    : Exception($"Rate limit exceeded for '{key}'.");

public sealed class RateLimitBehavior<TRequest, TResponse>(IRateLimiter limiter)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRateLimited
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var admitted = await limiter
            .TryAcquireAsync(request.RateLimitKey, cancellationToken)
            .ConfigureAwait(false);

        if (!admitted)
        {
            throw new RateLimitExceededException(request.RateLimitKey);
        }

        return await next(cancellationToken).ConfigureAwait(false);
    }
}
```

### Concurrency throttle

Bounds how many marked requests run at once. The gate is a static in a generic type, so each
request type gets its own four slots — see [§1](#1-reading-this-page).

```csharp
public interface IThrottled;

public sealed class ThrottleBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IThrottled
{
    private static readonly SemaphoreSlim Gate = new(initialCount: 4, maxCount: 4);

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await next(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }
}
```

---

## 4. Caching and deduplication

Behaviors that make the handler run less often.

### Read-through cache

The canonical skip: a hit returns a `TResponse` that already exists, a miss runs the handler and
stores the result.

```csharp
public interface ICacheable
{
    string CacheKey { get; }
    TimeSpan CacheDuration { get; }
}

public sealed class CachingBehavior<TRequest, TResponse>(ICache cache)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : ICacheable
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (cache.TryGet<TResponse>(request.CacheKey, out var cached))
        {
            return cached;                            // handler never runs
        }

        var response = await next(cancellationToken).ConfigureAwait(false);
        cache.Set(request.CacheKey, response, request.CacheDuration);

        return response;
    }
}
```

### Cache eviction

The write side of the cache pair: a successful command evicts the read keys it invalidates.
Eviction happens *after* `next`, so a failed command leaves the cache untouched.

```csharp
public interface ICacheEvicting
{
    IReadOnlyList<string> KeysToEvict { get; }
}

public sealed class CacheEvictionBehavior<TRequest, TResponse>(ICache cache)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : ICacheEvicting
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var response = await next(cancellationToken).ConfigureAwait(false);

        foreach (var key in request.KeysToEvict)
        {
            cache.Remove(key);
        }

        return response;
    }
}
```

### Request coalescing

Stampede protection: when fifty identical queries arrive together, one runs the handler and
forty-nine await the same task. The in-flight map is a static in a generic type — partitioned by
closed request type for free.

```csharp
public interface ICoalescable
{
    string CoalescingKey { get; }
}

public sealed class CoalescingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : ICoalescable
{
    private static readonly ConcurrentDictionary<string, Task<TResponse>> InFlight = new();

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var key = request.CoalescingKey;
        var completion = new TaskCompletionSource<TResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var winner = InFlight.GetOrAdd(key, completion.Task);

        if (!ReferenceEquals(winner, completion.Task))
        {
            return await winner.ConfigureAwait(false);   // follower: share the leader's result
        }

        try
        {
            var response = await next(cancellationToken).ConfigureAwait(false);
            completion.SetResult(response);

            return response;
        }
        catch (Exception exception)
        {
            completion.SetException(exception);
            _ = completion.Task.Exception;               // observed even with no followers

            throw;
        }
        finally
        {
            InFlight.TryRemove(key, out _);
        }
    }
}
```

Three caveats, all inherent to sharing:

- The leader's cancellation cancels every follower — they are awaiting its task.
- The shared response object crosses scopes; coalesce only responses that are immutable.
- Followers skip the *downstream* chain entirely, including behaviors registered inside this one.

### Idempotency

Replays the stored response for a key already seen, so a retried client request does not charge
the card twice:

```csharp
public interface IIdempotent
{
    string IdempotencyKey { get; }
}

public sealed class IdempotencyBehavior<TRequest, TResponse>(IIdempotencyStore store)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IIdempotent
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var existing = await store
            .TryGetAsync<TResponse>(request.IdempotencyKey, cancellationToken)
            .ConfigureAwait(false);

        if (existing.Found)
        {
            return existing.Value!;
        }

        var response = await next(cancellationToken).ConfigureAwait(false);

        await store
            .SaveAsync(request.IdempotencyKey, response, cancellationToken)
            .ConfigureAwait(false);

        return response;
    }
}
```

---

## 5. Resilience

Behaviors that change what failure looks like.

### Timeout

Substitute a linked token, and translate the resulting cancellation into `TimeoutException` so
callers can tell "the deadline passed" from "the caller gave up" — the `when` clause is what
keeps genuine caller cancellation flowing through untouched.

```csharp
public interface IHasTimeout
{
    TimeSpan Timeout { get; }
}

public sealed class TimeoutBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IHasTimeout
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);

        try
        {
            return await next(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"'{typeof(TRequest).Name}' did not complete within {request.Timeout}.");
        }
    }
}
```

Link, never replace: a bare `new CancellationTokenSource()` would discard the caller's
cancellation and leave the handler running after the client has disconnected.

### Retry

Exponential backoff with full jitter, catching only the transient type, rethrowing the original
exception with its stack intact when attempts run out. Every attempt re-runs the whole downstream
chain — that is [the sharpest lever](behaviors.md#1-the-six-levers) on the board.

```csharp
public interface IRetryable
{
    int MaxAttempts { get; }
}

public sealed class RetryBehavior<TRequest, TResponse>(
    ILogger<RetryBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRetryable
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
            catch (TransientException exception) when (attempt < request.MaxAttempts)
            {
                var backoff = TimeSpan.FromMilliseconds(Random.Shared.Next(50, 100 << attempt));

                logger.LogWarning(
                    exception,
                    "Attempt {Attempt} of {Request} failed; retrying in {Backoff}",
                    attempt, typeof(TRequest).Name, backoff);

                await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
```

### Conflict retry

The optimistic-concurrency special case: on a version conflict there is no point backing off —
the fix is to re-read and re-apply, which is exactly what re-running the downstream chain does.

```csharp
public interface IRetryOnConflict;

public sealed class ConflictRetryBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRetryOnConflict
{
    private const int MaxAttempts = 3;

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
            catch (ConcurrencyConflictException) when (attempt < MaxAttempts)
            {
                // Loop immediately: the next attempt re-reads current state.
            }
        }
    }
}
```

### Circuit breaker

Stops hammering a dependency that is already down. Register as a **singleton**; with instance
fields on an open generic that means one circuit per request type — see
[§1](#1-reading-this-page).

```csharp
public interface ICircuitProtected;

public sealed class CircuitOpenException(string requestType)
    : Exception($"The circuit for '{requestType}' is open; the request was not attempted.");

public sealed class CircuitBreakerBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : ICircuitProtected
{
    private const int FailureThreshold = 5;
    private static readonly TimeSpan BreakDuration = TimeSpan.FromSeconds(30);

    private readonly Lock _gate = new();
    private int _consecutiveFailures;
    private DateTimeOffset _openUntil;

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (DateTimeOffset.UtcNow < _openUntil)
            {
                throw new CircuitOpenException(typeof(TRequest).Name);
            }
        }

        try
        {
            var response = await next(cancellationToken).ConfigureAwait(false);

            lock (_gate)
            {
                _consecutiveFailures = 0;
            }

            return response;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            lock (_gate)
            {
                if (++_consecutiveFailures >= FailureThreshold)
                {
                    _openUntil = DateTimeOffset.UtcNow + BreakDuration;
                    _consecutiveFailures = 0;
                }
            }

            throw;
        }
    }
}
```

```csharp
cfg.AddOpenBehavior(typeof(CircuitBreakerBehavior<,>), ServiceLifetime.Singleton);
```

Cancellation is excluded from the failure count on purpose: a caller giving up says nothing
about the health of the dependency.

### Fallback

A degraded answer instead of an exception. This needs a constructible `TResponse`, which is the
[short-circuit problem](behaviors.md#4-the-short-circuit-problem) again — solved here with a
static abstract member on the response type.

```csharp
public interface IHasFallback<TSelf>
    where TSelf : IHasFallback<TSelf>
{
    static abstract TSelf Fallback { get; }
}

public sealed class FallbackBehavior<TRequest, TResponse>(
    ILogger<FallbackBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
    where TResponse : IHasFallback<TResponse>
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
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "{Request} failed; serving fallback", typeof(TRequest).Name);

            return TResponse.Fallback;
        }
    }
}
```

Never swallow `OperationCanceledException` into a fallback: a cancelled caller is not waiting
for the answer.

### Hedging

If the first attempt has not answered within the hedge delay, launch a second and take whichever
finishes first. Strictly for **idempotent reads through stateless handlers**: both attempts run
the full downstream chain concurrently, and they share the resolved handler instances of this
send.

```csharp
public interface IHedged
{
    TimeSpan HedgeAfter { get; }
}

public sealed class HedgingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IHedged
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        using var hedgeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var primary = next(hedgeCts.Token);
        var trigger = Task.Delay(request.HedgeAfter, hedgeCts.Token);

        if (await Task.WhenAny(primary, trigger).ConfigureAwait(false) == primary)
        {
            await hedgeCts.CancelAsync().ConfigureAwait(false);

            return await primary.ConfigureAwait(false);
        }

        var hedge = next(hedgeCts.Token);
        var winner = await Task.WhenAny(primary, hedge).ConfigureAwait(false);

        await hedgeCts.CancelAsync().ConfigureAwait(false);
        Observe(winner == primary ? hedge : primary);

        return await winner.ConfigureAwait(false);
    }

    // The losing attempt still faults after cancellation; its exception must be observed
    // somewhere or it surfaces as an UnobservedTaskException.
    private static void Observe(Task<TResponse> loser)
        => _ = loser.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
}
```

`WhenAny` takes the first *completion*, so a fast failure beats a slow success. A production
hedging policy wants more nuance than this — but this is the shape.

---

## 6. Consistency

Behaviors that hold the handler's work together.

### Transaction

Commit on success, roll back on anything else. No `catch` is needed: disposing an uncommitted
transaction rolls it back, which is exactly the behavior you want and one fewer branch to get
wrong.

```csharp
public interface ITransactional;

public sealed class TransactionBehavior<TRequest, TResponse>(IUnitOfWork unitOfWork)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : ITransactional
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);

        var response = await next(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return response;
    }
}
```

### Unit-of-work save

The lighter sibling: no explicit transaction, just `SaveChanges` after a successful handler, so
handlers mutate the model and never remember to flush it.

```csharp
public interface IMutating;

public sealed class SaveChangesBehavior<TRequest, TResponse>(IUnitOfWork unitOfWork)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IMutating
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var response = await next(cancellationToken).ConfigureAwait(false);

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return response;
    }
}
```

### Outbox staging

After the handler runs, stage the integration events it produced into an outbox table. Sitting
*inside* a transaction behavior makes the staging atomic with the handler's writes; a separate
process relays the outbox to the broker.

```csharp
public sealed class OutboxBehavior<TRequest, TResponse>(
    IOutbox outbox,
    IIntegrationEventCollector pending)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var response = await next(cancellationToken).ConfigureAwait(false);

        foreach (var message in pending.Drain())
        {
            await outbox.StageAsync(message, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }
}
```

### Domain event dispatch

Aggregates raise events into a scoped collector during handling; the behavior drains and
publishes them once the handler returns. The `while` loop matters: a notification handler that
raises further events gets those delivered too.

```csharp
public sealed class DomainEventsBehavior<TRequest, TResponse>(
    IDomainEventCollector events,
    IPublisher publisher)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var response = await next(cancellationToken).ConfigureAwait(false);

        while (events.TryDequeue(out var domainEvent))
        {
            await publisher.Publish(domainEvent, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }
}
```

Whether this sits inside the transaction (events commit or vanish with the handler's writes) or
outside it (events fire only after commit) changes the semantics entirely. Both are legitimate;
pick one on purpose.

### Distributed lock

One holder per key across the whole cluster, released even when the handler throws. Watch lock
lifetime against handler latency — a lock that expires mid-handler is worse than no lock.

```csharp
public interface ILockable
{
    string LockKey { get; }
}

public sealed class DistributedLockBehavior<TRequest, TResponse>(IDistributedLockProvider locks)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : ILockable
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var handle = await locks.AcquireAsync(request.LockKey, cancellationToken).ConfigureAwait(false);

        await using (handle.ConfigureAwait(false))
        {
            return await next(cancellationToken).ConfigureAwait(false);
        }
    }
}
```

---

## 7. Ambient context

Behaviors that establish the world the handler runs in.

### Correlation

An `AsyncLocal` correlation id plus a logging scope: everything logged below this behavior —
inner behaviors, the handler, services the handler calls — carries the id, and nested sends
reuse the one already flowing.

```csharp
public static class CorrelationContext
{
    private static readonly AsyncLocal<string?> Current = new();

    public static string? Id
    {
        get => Current.Value;
        set => Current.Value = value;
    }
}

public sealed class CorrelationBehavior<TRequest, TResponse>(
    ILogger<CorrelationBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var id = CorrelationContext.Id ??= Guid.NewGuid().ToString("N");

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id }))
        {
            return await next(cancellationToken).ConfigureAwait(false);
        }
    }
}
```

`AsyncLocal` values flow *down* the async call tree and not back out, which is exactly the
scoping a per-send id needs.

### Culture pinning

Format the handler's output in the request's culture, and put the ambient culture back no matter
how the handler exits:

```csharp
public interface ICultureSensitive
{
    string Culture { get; }
}

public sealed class CultureBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : ICultureSensitive
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var culture = CultureInfo.GetCultureInfo(request.Culture);
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;

        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        try
        {
            return await next(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }
}
```

### Request stamping

Fill in the fields the client must never be trusted to supply — the acting user, the receipt
time. Needs a settable property, so records with `init` setters do not qualify; if the behavior
only ever mutates and never short-circuits, an
[`IRequestPreProcessor<>`](usage.md#9-pre--and-post-processors) says the same thing with less
machinery.

```csharp
public interface IStamped
{
    string? InitiatedBy { get; set; }
}

public sealed class StampingBehavior<TRequest, TResponse>(ICurrentUser user)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IStamped
{
    public Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        request.InitiatedBy = user.UserName;

        return next(cancellationToken);
    }
}
```

### Input normalisation

Clamp instead of reject, for inputs where any value can be made valid — page sizes being the
classic:

```csharp
public interface IPagedRequest
{
    int PageSize { get; set; }
}

public sealed class PageSizeClampBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IPagedRequest
{
    private const int MaxPageSize = 200;

    public Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        request.PageSize = Math.Clamp(request.PageSize, 1, MaxPageSize);

        return next(cancellationToken);
    }
}
```

---

## 8. Response shaping

Behaviors that change what the caller receives.

### Exception mapping

Failures become typed results instead of thrown exceptions, for the requests whose response type
opts in with a static abstract factory. The full derivation of this pattern is in
[the short-circuit problem](behaviors.md#4-the-short-circuit-problem).

```csharp
public interface IResult<TSelf>
    where TSelf : IResult<TSelf>
{
    static abstract TSelf FromError(string error);
}

public sealed class ResultMappingBehavior<TRequest, TResponse>(
    ILogger<ResultMappingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
    where TResponse : IResult<TResponse>
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
        catch (DomainException exception)
        {
            logger.LogWarning(exception, "{Request} failed", typeof(TRequest).Name);

            return TResponse.FromError(exception.Message);
        }
    }
}
```

### Response redaction

The same response, with less in it, depending on who is asking. The response type owns its own
redaction, so the behavior never needs to know which fields are sensitive.

```csharp
public interface IRedactable<TSelf>
    where TSelf : IRedactable<TSelf>
{
    TSelf Redact();
}

public sealed class RedactionBehavior<TRequest, TResponse>(ICurrentUser user)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
    where TResponse : IRedactable<TResponse>
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var response = await next(cancellationToken).ConfigureAwait(false);

        return user.IsInRole("Admin") ? response : response.Redact();
    }
}
```

---

## 9. Operational and experimental

Behaviors for proving the system out, not for serving it.

### Chaos injection

Random extra latency and random faults, so resilience behaviors get exercised before production
exercises them. Register it only in test and staging composition roots; the `Enabled` check is a
second line of defence, not the first.

```csharp
public sealed record ChaosOptions(bool Enabled, double FaultProbability, TimeSpan MaxExtraLatency);

public sealed class ChaosException() : Exception("Injected chaos fault.");

public sealed class ChaosBehavior<TRequest, TResponse>(ChaosOptions options)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            return await next(cancellationToken).ConfigureAwait(false);
        }

        if (options.MaxExtraLatency > TimeSpan.Zero)
        {
            var extra = Random.Shared.Next((int)options.MaxExtraLatency.TotalMilliseconds);
            await Task.Delay(extra, cancellationToken).ConfigureAwait(false);
        }

        if (Random.Shared.NextDouble() < options.FaultProbability)
        {
            throw new ChaosException();
        }

        return await next(cancellationToken).ConfigureAwait(false);
    }
}
```

### Shadowing

Dark-launch a rewritten handler: serve the old answer, send the new implementation the same
request in the background, and log divergence. The background send gets a **fresh scope** — the
current one dies when this send returns — and a detached token, because the caller's cancellation
must not kill the comparison.

```csharp
public interface IShadowed<TResponse>
{
    IRequest<TResponse> CreateShadowRequest();
}

public sealed class ShadowBehavior<TRequest, TResponse>(
    IServiceScopeFactory scopeFactory,
    ILogger<ShadowBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IShadowed<TResponse>
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var response = await next(cancellationToken).ConfigureAwait(false);
        var shadow = request.CreateShadowRequest();

        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();

                var sender = scope.ServiceProvider.GetRequiredService<ISender>();
                var shadowResponse = await sender.Send(shadow, CancellationToken.None).ConfigureAwait(false);

                if (!EqualityComparer<TResponse>.Default.Equals(response, shadowResponse))
                {
                    logger.LogWarning("{Request}: shadow response diverged", typeof(TRequest).Name);
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "{Request}: shadow send failed", typeof(TRequest).Name);
            }
        });

        return response;
    }
}
```

Two rules keep this safe: the shadow request type must not itself implement `IShadowed<>` (that
is an infinite mirror), and nothing thrown by the shadow may ever reach the caller — hence the
catch-all inside the background task and nowhere else on this page.

### Deferred execution

Acknowledge now, execute later. Only meaningful for `Unit`-returning requests — there is no
response to defer — which is why `IDeferred` extends `IRequest` and the container only ever
closes this behavior with `TResponse = Unit`, making the cast safe.

```csharp
public interface IDeferred : IRequest;

public sealed class DeferredBehavior<TRequest, TResponse>(IDeferredQueue queue)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IDeferred
{
    public Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        // Re-entry from the drain worker: this send *is* the deferred execution.
        if (queue.IsDraining)
        {
            return next(cancellationToken);
        }

        queue.Enqueue(request);

        return Task.FromResult((TResponse)(object)Unit.Value);
    }
}
```

The drain worker — a hosted service — dequeues, creates a scope, flags itself as draining, and
re-sends the request through `ISender`; the flag lets the send pass through this behavior and
reach the handler. Be honest about what this buys: the caller is acknowledged before the work
runs, at-most-once is the delivery guarantee if the process dies with items queued, and failures
happen after the caller has already been told "ok" — the worker needs its own failure story.

---

## 10. Stream behaviors

The same ideas, inside an enumeration. Everything here implements
`IStreamPipelineBehavior<TRequest, TResponse>`, registers with `AddOpenStreamBehavior`, and
inherits the three stream rules from [the behaviors guide](behaviors.md#6-stream-behaviors):
`[EnumeratorCancellation]` always, cleanup in `finally`, and no buffering the whole stream.

One structural note up front: **a stream behavior cannot change the element type.** Turning a
stream of `T` into a stream of `List<T>` batches is the handler's job; a behavior can only
reshape *when* elements of the same type flow.

### Stream timing

Element count and total duration, recorded in a `finally` so early consumer break-out and
mid-stream failure still get measured:

```csharp
public sealed class StreamTimingBehavior<TRequest, TResponse>(
    ILogger<StreamTimingBehavior<TRequest, TResponse>> logger)
    : IStreamPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async IAsyncEnumerable<TResponse> Handle(
        TRequest request,
        StreamHandlerDelegate<TResponse> next,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        var count = 0;

        try
        {
            await foreach (var item in next(cancellationToken)
                .ConfigureAwait(false)
                .WithCancellation(cancellationToken))
            {
                count++;

                yield return item;
            }
        }
        finally
        {
            logger.LogInformation(
                "{Request} streamed {Count} items in {Elapsed}",
                typeof(TRequest).Name, count, Stopwatch.GetElapsedTime(start));
        }
    }
}
```

### Stall watchdog

A per-gap deadline rather than a whole-stream timeout: `CancelAfter` re-arms after every
element, so a stream may run for an hour as long as it never goes quiet for more than `MaxGap`.

```csharp
public interface IGapLimited
{
    TimeSpan MaxGap { get; }
}

public sealed class StreamWatchdogBehavior<TRequest, TResponse>
    : IStreamPipelineBehavior<TRequest, TResponse>
    where TRequest : IGapLimited
{
    public async IAsyncEnumerable<TResponse> Handle(
        TRequest request,
        StreamHandlerDelegate<TResponse> next,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        watchdog.CancelAfter(request.MaxGap);

        await foreach (var item in next(watchdog.Token)
            .ConfigureAwait(false)
            .WithCancellation(watchdog.Token))
        {
            watchdog.CancelAfter(request.MaxGap);   // re-arm for the next element

            yield return item;
        }
    }
}
```

The clock keeps running while the consumer processes a yielded element, so a slow *consumer*
also trips the watchdog. That is a caveat or a feature, depending on what you are guarding.

### Pacing

A minimum interval between elements — rate limiting for a downstream consumer that cannot keep
up with a fast producer:

```csharp
public interface IPaced
{
    TimeSpan MinInterval { get; }
}

public sealed class StreamPacingBehavior<TRequest, TResponse>
    : IStreamPipelineBehavior<TRequest, TResponse>
    where TRequest : IPaced
{
    public async IAsyncEnumerable<TResponse> Handle(
        TRequest request,
        StreamHandlerDelegate<TResponse> next,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in next(cancellationToken)
            .ConfigureAwait(false)
            .WithCancellation(cancellationToken))
        {
            yield return item;

            await Task.Delay(request.MinInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}
```

### Sampling

Every Nth element, for consumers that want the shape of the stream rather than all of it —
progress reporting over a bulk export, live charts over a firehose. Deliberately lossy.

```csharp
public interface ISampled
{
    int SampleEvery { get; }
}

public sealed class StreamSamplingBehavior<TRequest, TResponse>
    : IStreamPipelineBehavior<TRequest, TResponse>
    where TRequest : ISampled
{
    public async IAsyncEnumerable<TResponse> Handle(
        TRequest request,
        StreamHandlerDelegate<TResponse> next,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var index = 0;

        await foreach (var item in next(cancellationToken)
            .ConfigureAwait(false)
            .WithCancellation(cancellationToken))
        {
            if (index++ % request.SampleEvery == 0)
            {
                yield return item;
            }
        }
    }
}
```

### Chunked release

Elements flow one at a time but *arrive* in bursts of `ChunkSize` — micro-batching the timing
without touching the type. The trailing loop flushes the final partial chunk.

```csharp
public interface IChunkReleased
{
    int ChunkSize { get; }
}

public sealed class ChunkedReleaseBehavior<TRequest, TResponse>
    : IStreamPipelineBehavior<TRequest, TResponse>
    where TRequest : IChunkReleased
{
    public async IAsyncEnumerable<TResponse> Handle(
        TRequest request,
        StreamHandlerDelegate<TResponse> next,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new List<TResponse>(request.ChunkSize);

        await foreach (var item in next(cancellationToken)
            .ConfigureAwait(false)
            .WithCancellation(cancellationToken))
        {
            buffer.Add(item);

            if (buffer.Count == request.ChunkSize)
            {
                foreach (var buffered in buffer)
                {
                    yield return buffered;
                }

                buffer.Clear();
            }
        }

        foreach (var buffered in buffer)
        {
            yield return buffered;
        }
    }
}
```

---

## See also

- [Behaviors](behaviors.md) — the levers, a stack that works, ordering, costs, and traps.
- [Usage § 8](usage.md#8-pipeline-behaviors) — how to write and register a behavior.
- [Usage § 9](usage.md#9-pre--and-post-processors) — when a processor is the better fit.
- [Abstractions](abstractions.md#ipipelinebehaviortrequest-tresponse) — the contract itself.
- [Performance](performance.md) — what the chain costs.
