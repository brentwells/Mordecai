# Pipeline Behaviors

A catalogue of what you can build with `IPipelineBehavior<TRequest, TResponse>`, organised by what
each one does to the pipeline rather than by what it is called.

[The usage guide](usage.md#8-pipeline-behaviors) covers the basics — how to write one, how to
register it, how ordering works. This page assumes that and goes wide.

```
Behavior A  ──►  Behavior B  ──►  Handler  ──►  B returns  ──►  A returns
```

---

## Contents

1. [The six levers](#1-the-six-levers)
2. [A stack that works](#2-a-stack-that-works)
3. [Targeting](#3-targeting)
4. [The short-circuit problem](#4-the-short-circuit-problem)
5. [The catalogue](#5-the-catalogue)
6. [Stream behaviors](#6-stream-behaviors)
7. [What a behavior cannot do](#7-what-a-behavior-cannot-do)
8. [What a behavior costs](#8-what-a-behavior-costs)
9. [Traps](#9-traps)

---

## 1. The six levers

A behavior receives the request, a continuation, and a token. Everything you can build comes from
six things you can do with them. Knowing which lever a behavior pulls tells you where in the chain
it belongs and what it can and cannot do.

| Lever | Shape | Buys you |
| --- | --- | --- |
| **Run before `next`** | `Inspect(request); return next(ct);` | Validation, authorization, enrichment |
| **Run after `next`** | `var r = await next(ct); Record(r); return r;` | Auditing, metrics, response shaping |
| **Skip `next`** | `return cached;` | Caching, idempotency, kill switches |
| **Wrap `next`** | `try { … } catch { … } finally { … }` | Transactions, exception mapping, cleanup |
| **Substitute the token** | `return next(other.Token);` | Timeouts, linked cancellation |
| **Call `next` repeatedly** | `for (…) { try { return await next(ct); } … }` | Retry — with real caveats, see [§5.4](#54-failure-handling) |

The last one deserves a warning up front. **Calling `next` more than once re-runs the entire
downstream chain**, not just the handler: every inner behavior, every pre-processor, and the handler
itself run again per attempt. That is what makes retry-above-transaction work, and it is also why a
behavior that calls `next` twice by accident is a genuine bug rather than a wasted cycle.

---

## 2. A stack that works

Behaviors nest in registration order, **first registered outermost**. Order is not cosmetic — most
of the adjacencies below exist for a reason, and getting one backwards produces something that looks
fine and is quietly wrong.

```csharp
services.AddMordecai(cfg =>
{
    cfg.RegisterServicesFromAssemblyContaining<Program>();

    cfg.AddOpenBehavior(typeof(ExceptionMappingBehavior<,>));   // outermost
    cfg.AddOpenBehavior(typeof(LoggingBehavior<,>));
    cfg.AddOpenBehavior(typeof(AuthorizationBehavior<,>));
    cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
    cfg.AddOpenBehavior(typeof(CachingBehavior<,>));
    cfg.AddOpenBehavior(typeof(RetryBehavior<,>));
    cfg.AddOpenBehavior(typeof(TransactionBehavior<,>));        // innermost
});
```

Why that order, adjacency by adjacency:

- **Exception mapping above logging**, not below. If mapping sits *inside* logging it converts the
  exception into an ordinary response before logging ever sees it, and your logs record a clean
  success for every failed request. This is the single most common ordering mistake, and it is
  invisible until you go looking for an error you know happened.
- **Logging above everything else** so it captures authorization denials, validation failures, and
  the total elapsed time including retries.
- **Authorization above validation** so an unauthorized caller does not learn which fields of your
  request are invalid.
- **Validation above caching** so a malformed request is rejected rather than served from cache.
- **Caching above retry** so a cache hit costs nothing; there is no point retrying a lookup you did
  not need to make.
- **Retry above the transaction** so each attempt gets a *fresh* transaction. Retrying inside a
  transaction that has already failed retries inside a doomed transaction — with EF Core the change
  tracker is poisoned and the retry cannot succeed.
- **Transaction innermost** so it is held for the shortest possible time and none of the work above
  it happens inside the transaction.

Pre- and post-processors are always innermost, inside every behavior above — see
[usage § 9](usage.md#9-pre--and-post-processors).

---

## 3. Targeting

The interface constrains `TRequest` to `notnull`, deliberately, **not** to `IRequest<TResponse>`.
That is what lets you constrain on your own markers and have the behavior apply to a subset.

This is resolved when the container closes the generic, so **a behavior that does not apply costs
nothing at runtime** — it is not resolved, not constructed, and not in the chain.

### Every request

```csharp
public sealed class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
```

Applies to everything, including void requests — an `IRequest` has `TResponse` of `Unit`, and open
behaviors see it like any other response type.

### Requests carrying a marker

```csharp
public interface IAudited;

public sealed record PlaceOrder(int CustomerId) : IRequest<OrderResult>, IAudited;

public sealed class AuditBehavior<TRequest, TResponse>(IAuditLog audit)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IAudited
```

The cheapest and clearest targeting mechanism. Add the marker to the requests you want covered.

### A generic marker tied to the response

A CQRS split gives you targeting for free:

```csharp
public interface IQuery<out TResponse> : IRequest<TResponse>;
public interface ICommand<out TResponse> : IRequest<TResponse>;

public sealed class QueryCachingBehavior<TRequest, TResponse>(ICache cache)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IQuery<TResponse>
```

The constraint references *both* type parameters, and the container honours it: a command never
sees this behavior. Reads get cached, writes do not, and neither the requests nor the handlers know
the behavior exists.

### Constraining the response

```csharp
public sealed class ResultLoggingBehavior<TRequest, TResponse>(ILogger logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
    where TResponse : IResult
```

Applies only where the response implements your result marker. See
[§4](#4-the-short-circuit-problem) — this is also how an open behavior earns the ability to
short-circuit.

### One specific request

```csharp
services.AddMordecai(cfg =>
    cfg.AddBehavior<IPipelineBehavior<GetOrder, Order>, GetOrderOnlyBehavior>());
```

Closed registrations join the same ordered chain as open ones, interleaved exactly where you wrote
them. Prefer a constrained open behavior where you can; reach for this when a behavior is genuinely
bespoke to one request.

---

## 4. The short-circuit problem

Skipping `next` means returning a `TResponse` you produced yourself — and inside an open generic
behavior, `TResponse` is an unknown type you cannot construct. This constrains the design more than
most people expect, and it is worth understanding before you try to write an authorization behavior
that returns `Forbidden` instead of throwing.

You have three ways out.

**Get one from somewhere that is already typed.** A cache is the usual example: `TryGet<TResponse>`
hands you a value of the right type without you having to build one.

```csharp
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

**Constrain `TResponse` so you can build one.** A static abstract member gives the behavior a
factory, and this is the cleanest way to build result-returning pipelines:

```csharp
public interface IResult;

public interface IResult<TSelf> : IResult
    where TSelf : IResult<TSelf>
{
    static abstract TSelf FromError(string error);
}

public sealed record OrderResult(int OrderId, string? Error) : IResult<OrderResult>
{
    public static OrderResult FromError(string error) => new(0, error);
}

public sealed class ExceptionMappingBehavior<TRequest, TResponse>(ILogger logger)
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

            return TResponse.FromError(exception.Message);   // no throw, typed response
        }
    }
}
```

Requests whose response does not implement `IResult<>` simply never see this behavior — the
container filters it out on the constraint. You get one pipeline where result-returning requests
are mapped and everything else is left alone.

**Or throw.** If neither applies, the only exit from an open behavior is an exception, and that is
usually the right answer anyway: an unauthorized or invalid request is exceptional, and something
further out — an exception-mapping behavior, or ASP.NET Core's exception handler — turns it into a
response. Reserve short-circuiting for the cases where *not* running the handler is a success.

---

## 5. The catalogue

### 5.1 Observation

Behaviors that watch and never interfere. The safest category, and the one worth adding first.

**Logging** — see [usage § 8](usage.md#writing-one) for the canonical example.

**Metrics.** Note the `finally`: an exception must not lose the measurement.

```csharp
public sealed class MetricsBehavior<TRequest, TResponse>(IMetrics metrics)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var name = typeof(TRequest).Name;
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
            metrics.Record(name, outcome, Stopwatch.GetElapsedTime(start));
        }
    }
}
```

**Tracing.** One span per request, named for the request type:

```csharp
public sealed class TracingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private static readonly ActivitySource Source = new("Mordecai");

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        using var activity = Source.StartActivity($"Send {typeof(TRequest).Name}");

        try
        {
            var response = await next(cancellationToken).ConfigureAwait(false);
            activity?.SetStatus(ActivityStatusCode.Ok);

            return response;
        }
        catch (Exception exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            throw;
        }
    }
}
```

### 5.2 Guarding

Behaviors that decide whether the handler runs at all.

**Validation.** Collects every validator registered for the request type, so a request with no
validators passes straight through:

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

If a request has exactly one validator and you never need to short-circuit, an
[`IRequestPreProcessor<>`](usage.md#9-pre--and-post-processors) says the same thing with less
machinery.

**Authorization.** Throwing, because an open behavior cannot fabricate a `TResponse`:

```csharp
public interface IRequireRole
{
    string RequiredRole { get; }
}

public sealed class AuthorizationBehavior<TRequest, TResponse>(ICurrentUser user)
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

**Caching** — see [§4](#4-the-short-circuit-problem).

**Idempotency.** Replays the stored response for a key already seen, so a retried client request
does not charge the card twice:

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

### 5.3 Resources and lifecycle

**Transaction.** Commit on success, roll back on anything else. Register it innermost.

```csharp
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

No `catch` is needed: disposing an uncommitted transaction rolls it back, which is exactly the
behavior you want and one fewer branch to get wrong.

**Concurrency limiting.** The semaphore has to be shared across requests, so this behavior must be
a **singleton** — register it with an explicit lifetime and it will not accidentally follow a
`Scoped` default:

```csharp
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

```csharp
cfg.AddOpenBehavior(typeof(ThrottleBehavior<,>), ServiceLifetime.Singleton);
```

### 5.4 Failure handling

**Timeout.** The one case for substituting the token:

```csharp
public sealed class TimeoutBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        return await next(timeout.Token).ConfigureAwait(false);
    }
}
```

Link, never replace. A bare `new CancellationTokenSource()` would discard the caller's cancellation
and leave the handler running after the client has disconnected.

**Exception mapping** — see [§4](#4-the-short-circuit-problem).

**Retry.** Genuinely useful and genuinely sharp. Read the caveats below the code.

```csharp
public sealed class RetryBehavior<TRequest, TResponse>(ILogger logger)
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
                logger.LogWarning(exception, "Attempt {Attempt} failed, retrying", attempt);

                await Task
                    .Delay(TimeSpan.FromMilliseconds(100 * attempt), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }
}
```

Four things to get right, and each of them is a real outage if you do not:

- **Only the handler knows whether it is safe to retry.** Mark requests explicitly — `IRetryable`
  above — rather than retrying everything. Retrying a non-idempotent command sends the email twice.
- **Catch a narrow exception type.** `catch (Exception)` retries validation failures, authorization
  denials and `NullReferenceException`, none of which will succeed on the second attempt.
- **Filter on the attempt count in the `when` clause**, not with a `break` inside the `catch`. The
  version above rethrows the original exception with its stack intact once attempts run out; a
  `break` would need you to re-throw manually and it is easy to lose the original.
- **Register it above the transaction behavior.** Each attempt then gets a fresh transaction. Below
  it, every attempt after the first runs inside a transaction that has already failed.

Remember the lever: **each attempt re-runs the whole downstream chain** — inner behaviors,
pre-processors, handler. That is what makes fresh-transaction-per-attempt work, and it also means an
inner behavior with side effects runs once per attempt.

### 5.5 Reference

The rest, by lever, for when you want the shape rather than the code.

| Behavior | Lever | Note |
| --- | --- | --- |
| Correlation ID / tenant enrichment | Before | A pre-processor is usually the better fit |
| Response redaction | After | Constrain `TResponse` to something you can rewrite |
| Feature flag / kill switch | Skip | Same `TResponse` problem as authorization — usually throws |
| Distributed lock | Wrap | Acquire before `next`, release in `finally`; watch lock lifetime against handler latency |
| Fallback | Wrap | Needs a constructible `TResponse`, same as exception mapping |
| Circuit breaker | Wrap + skip | Shared state means singleton lifetime, as with throttling |
| Outbox dispatch | After | Runs inside the transaction, so register it below `TransactionBehavior` |

---

## 6. Stream behaviors

Stream requests use a **separate interface**. `IPipelineBehavior<,>` does not apply to
`IStreamRequest<T>`, and `IStreamPipelineBehavior<,>` does not apply to ordinary requests. Both
directions are enforced and tested; a stream behavior registered with `AddOpenBehavior` instead of
`AddOpenStreamBehavior` silently never runs.

The continuation is a `StreamHandlerDelegate<TResponse>` returning `IAsyncEnumerable<TResponse>`,
and your `Handle` is an async iterator:

```csharp
public sealed class StreamLoggingBehavior<TRequest, TResponse>(ILogger logger)
    : IStreamPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async IAsyncEnumerable<TResponse> Handle(
        TRequest request,
        StreamHandlerDelegate<TResponse> next,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var name = typeof(TRequest).Name;
        var count = 0;

        logger.LogInformation("{Request} stream starting", name);

        await foreach (var item in next(cancellationToken)
            .ConfigureAwait(false)
            .WithCancellation(cancellationToken))
        {
            count++;

            yield return item;
        }

        // Reached only if the consumer enumerated to the end. Breaking out early disposes the
        // iterator here instead, and this line never runs.
        logger.LogInformation("{Request} stream completed with {Count} items", name, count);
    }
}
```

```csharp
cfg.AddOpenStreamBehavior(typeof(StreamLoggingBehavior<,>));
```

Three things that are different about streams:

- **`[EnumeratorCancellation]` is not optional**, on behaviors exactly as on handlers. Without it a
  token supplied through `WithCancellation` is dropped and the stream becomes uncancellable.
- **Code after the `await foreach` is not guaranteed to run.** A consumer that `break`s out of the
  loop disposes the iterator, and everything after the loop is skipped. Anything that must happen —
  releasing a resource, recording a metric — belongs in a `finally`, not after the loop.
- **Nothing is buffered.** A behavior that needs to see all the elements before yielding any of them
  turns a stream into a list and throws away the reason you chose streaming. If you need that, you
  wanted `IRequest<IReadOnlyList<T>>`.

Useful stream behaviors: element logging and counting, per-element timeouts, rate limiting between
elements, and batching a fine-grained stream into chunks. Guarding and short-circuiting mostly do
not translate — by the time the first element arrives the work is already under way.

---

## 7. What a behavior cannot do

- **Wrap a notification.** `IPipelineBehavior` applies to requests only. Publishing runs handlers
  directly through the configured `INotificationPublisher`, with no middleware around them. To wrap
  notification handling, either put the logic in a base handler class or write a custom
  [`INotificationPublisher`](abstractions.md#inotificationpublisher).
- **Wrap a stream request.** Use `IStreamPipelineBehavior<,>`; see [§6](#6-stream-behaviors).
- **Change the request type.** You get a `TRequest` and the handler gets the same `TRequest`. To
  dispatch something else, inject `ISender` and send it — but be aware that goes through the whole
  pipeline again, including the behavior you are standing in.
- **Choose a different handler.** Handler selection happens below the pipeline.
- **See other behaviors.** Behaviors compose only through the response and the exception. If two
  need to share state, put it in a scoped service they both depend on.

---

## 8. What a behavior costs

From [the performance guide](performance.md#three-behaviors-cost-408-b-and-that-is-the-delegate-chain):
about **104 bytes per behavior per send** — one closure and one delegate for each link in the chain —
plus roughly 20 ns. Three behaviors take a send from 0 B and 24 ns to 408 B and 84 ns.

That is not waste to be reclaimed; a delegate-based pipeline captures state, and captured state is
an object. But it does mean behaviors are not free, and the cheapest behavior is one that never
enters the chain at all. If a request is hot enough for this to matter, constrain the behavior to a
marker interface rather than applying it to everything — a behavior that does not apply is filtered
out when the container closes the generic and costs exactly nothing.

Registering a behavior to run on every request and then having it check a condition and call `next`
is the expensive way to write what a type constraint expresses for free.

---

## 9. Traps

| Trap | Symptom | Fix |
| --- | --- | --- |
| `next()` with no token | Cancellation ignored below this behavior | Always `next(cancellationToken)` — the parameter is optional so it compiles |
| Exception mapping registered inside logging | Logs show success for failed requests | Register mapping outermost |
| Retry below the transaction | Every retry fails the same way | Register retry above the transaction |
| `catch (Exception)` in a retry | Validation failures retried three times | Catch the transient type only |
| `next` called twice by accident | Handler runs twice, duplicate side effects | Call it exactly once per path |
| Stream behavior registered with `AddOpenBehavior` | Behavior never runs, no error | Use `AddOpenStreamBehavior` |
| Missing `[EnumeratorCancellation]` | Stream will not cancel | Add it to the token parameter |
| Behavior expected to wrap a notification | Never runs | Behaviors are request-only; write a custom publisher |
| Shared state in a non-singleton behavior | Throttle or breaker does nothing | Pass an explicit `ServiceLifetime.Singleton` |
| Singleton behavior injecting a scoped service | *Cannot resolve scoped service from root provider* | Inject `IServiceScopeFactory`, or leave the behavior scoped |
| Work after `await foreach` in a stream behavior | Cleanup skipped when the consumer breaks early | Move it into a `finally` |

---

## See also

- [Usage § 8](usage.md#8-pipeline-behaviors) — the basics, registration, and ordering.
- [Usage § 9](usage.md#9-pre--and-post-processors) — when a processor is the better fit.
- [Usage § 13](usage.md#13-testing) — testing a behavior with a stub continuation.
- [Abstractions](abstractions.md#ipipelinebehaviortrequest-tresponse) — the contract itself.
- [Performance](performance.md) — what the chain costs.
