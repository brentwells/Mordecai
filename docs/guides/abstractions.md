# Mordecai Abstractions Guide

Every public type in `namespace Mordecai`, what it is for, and how to use it.

> **Status:** these contracts and the runtime that executes them both exist. `Mediator`, pipeline
> composition, streaming, the pre/post processors, and DI registration are implemented and tested;
> see [the usage guide](usage.md) for how to wire them up.

The running example is a small order-processing domain, so the pieces fit together rather than
standing alone.

---

## Contents

**Messages** — [IRequest&lt;TResponse&gt;](#irequesttresponse) · [IRequest](#irequest) ·
[IStreamRequest&lt;T&gt;](#istreamrequestt) · [INotification](#inotification) ·
[IBaseRequest](#ibaserequest)

**Handlers** — [IRequestHandler&lt;,&gt;](#irequesthandlertrequest-tresponse) ·
[IRequestHandler&lt;&gt;](#irequesthandlertrequest) ·
[IStreamRequestHandler&lt;,&gt;](#istreamrequesthandlertrequest-tresponse) ·
[INotificationHandler&lt;&gt;](#inotificationhandlertnotification)

**Pipeline** — [IPipelineBehavior&lt;,&gt;](#ipipelinebehaviortrequest-tresponse) ·
[RequestHandlerDelegate&lt;T&gt;](#requesthandlerdelegatetresponse) ·
[IStreamPipelineBehavior&lt;,&gt;](#istreampipelinebehaviortrequest-tresponse) ·
[StreamHandlerDelegate&lt;T&gt;](#streamhandlerdelegatetresponse) ·
[IRequestPreProcessor&lt;&gt;](#irequestpreprocessortrequest) ·
[IRequestPostProcessor&lt;,&gt;](#irequestpostprocessortrequest-tresponse)

**Entry points** — [ISender](#isender) · [IPublisher](#ipublisher) · [IMediator](#imediator)

**Publishing strategy** — [INotificationPublisher](#inotificationpublisher) ·
[NotificationHandlerExecutor](#notificationhandlerexecutor)

**Values** — [Unit](#unit)

---

## The two message shapes

Mordecai routes two fundamentally different kinds of message, and picking the wrong one is the most
common modelling mistake:

| | Handlers | Return value | Use for |
| --- | --- | --- | --- |
| **Request** | Exactly one | Yes | "Do this" / "get me that" — you need an answer or a guarantee |
| **Notification** | Zero or more | No | "This happened" — announce a fact, callers don't care who listens |

A request with no handler is an error. A notification with no handlers is normal.

---

# Messages

## `IRequest<TResponse>`

A message handled by exactly one handler that returns a `TResponse`.

```csharp
public interface IRequest<out TResponse> : IBaseRequest;
```

You implement this on the message itself. Records work well — requests should be immutable data.

```csharp
using Mordecai;

public sealed record GetOrder(int OrderId) : IRequest<Order>;

public sealed record PlaceOrder(int CustomerId, IReadOnlyList<int> ProductIds)
    : IRequest<PlaceOrderResult>;
```

`TResponse` is covariant (`out`), so an `IRequest<PremiumOrder>` is usable where an
`IRequest<Order>` is expected.

Both commands (`PlaceOrder`) and queries (`GetOrder`) use this interface. Mordecai does not enforce
a CQRS split — if you want one, express it with your own marker interfaces:

```csharp
public interface IQuery<out TResponse> : IRequest<TResponse>;
public interface ICommand<out TResponse> : IRequest<TResponse>;

public sealed record GetOrder(int OrderId) : IQuery<Order>;
```

That also gives you something to constrain a pipeline behavior on — see
[IPipelineBehavior](#ipipelinebehaviortrequest-tresponse).

## `IRequest`

A request that returns nothing.

```csharp
public interface IRequest : IRequest<Unit>;
```

This is `IRequest<Unit>` with a friendlier name. There is no separate void path in the engine —
"returns nothing" is modelled as "returns [`Unit`](#unit)", a zero-size struct, so it costs no
allocation.

```csharp
public sealed record CancelOrder(int OrderId) : IRequest;
```

The consequence shows up in the handler: it still returns `Task<Unit>`. See
[IRequestHandler&lt;TRequest&gt;](#irequesthandlertrequest).

## `IStreamRequest<T>`

A request whose handler produces a sequence of responses over time rather than one value.

```csharp
public interface IStreamRequest<out TResponse>;
```

```csharp
public sealed record StreamOrdersForCustomer(int CustomerId) : IStreamRequest<Order>;
```

Reach for this when the result set is large, unbounded, or produced incrementally — paging through
a big table, tailing a feed, relaying tokens from a model. If the result is a list that comfortably
fits in memory, use `IRequest<IReadOnlyList<T>>` instead; a stream adds async-enumerator machinery
you would not be using.

Note it does **not** extend `IBaseRequest`. Stream requests are a separate routing path.

## `INotification`

An event broadcast to every registered handler.

```csharp
public interface INotification;
```

```csharp
public sealed record OrderPlaced(int OrderId, int CustomerId, DateTimeOffset PlacedAt)
    : INotification;
```

Name notifications in the past tense — they report something that already happened. The publisher
gets no return value and, by default, no indication of how many handlers ran. If you need an answer
back, you wanted a request.

## `IBaseRequest`

```csharp
public interface IBaseRequest;
```

A non-generic marker that every `IRequest<TResponse>` carries. It exists so the dispatcher and the
weakly-typed `Send(object)` overload can recognise a request without knowing its response type at
compile time.

**You never implement this directly.** Implement `IRequest<TResponse>` or `IRequest`, which bring it
along. It is useful in a type check:

```csharp
if (message is IBaseRequest)
{
    // Route through Send rather than Publish.
}
```

---

# Handlers

## `IRequestHandler<TRequest, TResponse>`

The single handler for a request.

```csharp
public interface IRequestHandler<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}
```

```csharp
public sealed class GetOrderHandler(IOrderRepository repository)
    : IRequestHandler<GetOrder, Order>
{
    public async Task<Order> Handle(GetOrder request, CancellationToken cancellationToken)
    {
        var order = await repository
            .FindAsync(request.OrderId, cancellationToken)
            .ConfigureAwait(false);

        return order ?? throw new OrderNotFoundException(request.OrderId);
    }
}
```

Dependencies come through the constructor and are resolved by the container. Registering two
handlers for the same request is a configuration error — the generator will report it at compile
time rather than letting one silently win.

Always flow `cancellationToken` into everything you await.

## `IRequestHandler<TRequest>`

Handler for a request that returns nothing.

```csharp
public interface IRequestHandler<in TRequest> : IRequestHandler<TRequest, Unit>
    where TRequest : IRequest<Unit>;
```

The signature you implement still returns `Task<Unit>`, because that is what the base interface
declares. Return the cached `Unit.Task` — never `Task.FromResult(Unit.Value)`, which allocates a
fresh task every call:

```csharp
public sealed class CancelOrderHandler(IOrderRepository repository)
    : IRequestHandler<CancelOrder>
{
    public async Task<Unit> Handle(CancelOrder request, CancellationToken cancellationToken)
    {
        await repository.CancelAsync(request.OrderId, cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
```

In an `async` method `return Unit.Value;` is correct and free — the state machine already owns the
task. `Unit.Task` is for the synchronous case:

```csharp
public sealed class LogOnlyHandler : IRequestHandler<CancelOrder>
{
    public Task<Unit> Handle(CancelOrder request, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Cancelling {request.OrderId}");
        return Unit.Task;   // cached; no allocation
    }
}
```

## `IStreamRequestHandler<TRequest, TResponse>`

Handler for a stream request.

```csharp
public interface IStreamRequestHandler<in TRequest, out TResponse>
    where TRequest : IStreamRequest<TResponse>
{
    IAsyncEnumerable<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}
```

Note it is **not** `async Task<...>` — it returns the enumerable directly. Use an iterator method
with `[EnumeratorCancellation]` so the token reaches the loop:

```csharp
using System.Runtime.CompilerServices;

public sealed class StreamOrdersHandler(IOrderRepository repository)
    : IStreamRequestHandler<StreamOrdersForCustomer, Order>
{
    public async IAsyncEnumerable<Order> Handle(
        StreamOrdersForCustomer request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var page = 0;

        while (true)
        {
            var batch = await repository
                .GetPageAsync(request.CustomerId, page++, cancellationToken)
                .ConfigureAwait(false);

            if (batch.Count == 0)
            {
                yield break;
            }

            foreach (var order in batch)
            {
                yield return order;
            }
        }
    }
}
```

Without `[EnumeratorCancellation]`, a token passed to `WithCancellation(...)` at the call site is
silently ignored — a common and hard-to-spot bug.

## `INotificationHandler<TNotification>`

One of possibly many handlers for a notification.

```csharp
public interface INotificationHandler<in TNotification>
    where TNotification : INotification
{
    Task Handle(TNotification notification, CancellationToken cancellationToken);
}
```

Several handlers subscribe to the same event independently:

```csharp
public sealed class SendConfirmationEmail(IEmailService email)
    : INotificationHandler<OrderPlaced>
{
    public Task Handle(OrderPlaced notification, CancellationToken cancellationToken)
        => email.SendOrderConfirmationAsync(notification.OrderId, cancellationToken);
}

public sealed class UpdateSalesMetrics(IMetrics metrics)
    : INotificationHandler<OrderPlaced>
{
    public Task Handle(OrderPlaced notification, CancellationToken cancellationToken)
    {
        metrics.IncrementOrderCount();
        return Task.CompletedTask;
    }
}
```

Neither handler knows the other exists. Do not depend on ordering between them — if two things must
happen in sequence, that is one handler with two steps, or a request.

An unhandled exception in one handler propagates to the caller of `Publish` and, depending on the
[INotificationPublisher](#inotificationpublisher) strategy, may prevent later handlers from running.
If a handler is genuinely optional, catch inside it.

---

# Pipeline

## `IPipelineBehavior<TRequest, TResponse>`

Middleware wrapped around a request handler. This is where cross-cutting concerns live: logging,
validation, transactions, caching, retries, authorization.

```csharp
public interface IPipelineBehavior<in TRequest, TResponse>
    where TRequest : notnull
{
    Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken);
}
```

Behaviors nest, outermost first in registration order:

```
Behavior A  ──►  Behavior B  ──►  Handler  ──►  B returns  ──►  A returns
```

The constraint is `notnull`, **not** `IRequest<TResponse>`, which is deliberate — it lets one
open-generic behavior apply to every request in the application:

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
        var name = typeof(TRequest).Name;
        var start = Stopwatch.GetTimestamp();

        logger.LogInformation("Handling {Request}", name);

        try
        {
            var response = await next(cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "Handled {Request} in {Elapsed}", name, Stopwatch.GetElapsedTime(start));
            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Request} failed", name);
            throw;
        }
    }
}
```

**Short-circuiting.** Not calling `next` stops the pipeline — the handler never runs. That is how
caching and authorization work:

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
            return cached;   // handler never runs
        }

        var response = await next(cancellationToken).ConfigureAwait(false);
        cache.Set(request.CacheKey, response, request.CacheDuration);
        return response;
    }
}
```

Constraining `TRequest` narrows which requests a behavior applies to — here, only those implementing
your own `ICacheable`. This is the mechanism for targeting behaviors, and it is resolved at compile
time, so a behavior that does not apply costs nothing at runtime.

**Call `next` exactly once** on any path that does not short-circuit. Calling it twice runs the
handler twice.

## `RequestHandlerDelegate<TResponse>`

The continuation handed to a pipeline behavior.

```csharp
public delegate Task<TResponse> RequestHandlerDelegate<TResponse>(
    CancellationToken cancellationToken = default);
```

Invoking it runs the next behavior, or the handler itself at the end of the chain.

The token parameter is optional, so `next()` compiles — but **pass the token explicitly**:

```csharp
await next(cancellationToken).ConfigureAwait(false);   // do this
await next().ConfigureAwait(false);                    // silently drops cancellation
```

Passing it is what lets a behavior substitute a modified token, e.g. adding a timeout:

```csharp
public async Task<TResponse> Handle(
    TRequest request,
    RequestHandlerDelegate<TResponse> next,
    CancellationToken cancellationToken)
{
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(TimeSpan.FromSeconds(30));

    return await next(timeout.Token).ConfigureAwait(false);
}
```

## `IStreamPipelineBehavior<TRequest, TResponse>`

Middleware for stream requests.

```csharp
public interface IStreamPipelineBehavior<in TRequest, TResponse>
    where TRequest : notnull
{
    IAsyncEnumerable<TResponse> Handle(
        TRequest request,
        StreamHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken);
}
```

Separate from `IPipelineBehavior` because the shapes genuinely differ: there is no single response
to inspect, and the behavior sits inside the enumeration rather than around a single await. A
behavior here observes each element as it flows past:

```csharp
public sealed class StreamCountingBehavior<TRequest, TResponse>(IMetrics metrics)
    : IStreamPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async IAsyncEnumerable<TResponse> Handle(
        TRequest request,
        StreamHandlerDelegate<TResponse> next,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var count = 0;

        await foreach (var item in next(cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            count++;
            yield return item;
        }

        metrics.RecordStreamLength(typeof(TRequest).Name, count);
    }
}
```

Note the trailing code runs only if the consumer enumerates to completion. If they `break` early, it
never executes — put cleanup in a `finally`.

## `StreamHandlerDelegate<TResponse>`

```csharp
public delegate IAsyncEnumerable<TResponse> StreamHandlerDelegate<out TResponse>(
    CancellationToken cancellationToken = default);
```

The stream equivalent of `RequestHandlerDelegate<T>`. Calling it returns the downstream sequence; it
does not start work until enumerated.

## `IRequestPreProcessor<TRequest>`

Runs before the handler, after all outer behaviors. Register it with
`AddOpenRequestPreProcessor(typeof(YourPreProcessor<>))`; nothing invokes a pre-processor that has
not been registered.

```csharp
public interface IRequestPreProcessor<in TRequest>
    where TRequest : notnull
{
    Task Process(TRequest request, CancellationToken cancellationToken);
}
```

```csharp
public sealed class ValidationPreProcessor<TRequest>(IValidator<TRequest> validator)
    : IRequestPreProcessor<TRequest>
    where TRequest : notnull
{
    public async Task Process(TRequest request, CancellationToken cancellationToken)
    {
        var result = await validator.ValidateAsync(request, cancellationToken).ConfigureAwait(false);

        if (!result.IsValid)
        {
            throw new ValidationException(result.Errors);
        }
    }
}
```

**Use a behavior instead when** you need to short-circuit, inspect the response, wrap in a
`try`/`finally`, or hold a resource across the call. A pre-processor cannot do any of those — it
returns `Task`, not `Task<TResponse>`, so its only ways to affect the outcome are mutating the
request or throwing. That narrowness is the point: it keeps simple things simple.

## `IRequestPostProcessor<TRequest, TResponse>`

Runs after the handler returns successfully. Register it with
`AddOpenRequestPostProcessor(typeof(YourPostProcessor<,>))`.

```csharp
public interface IRequestPostProcessor<in TRequest, in TResponse>
    where TRequest : notnull
{
    Task Process(TRequest request, TResponse response, CancellationToken cancellationToken);
}
```

```csharp
public sealed class AuditPostProcessor<TRequest, TResponse>(IAuditLog audit)
    : IRequestPostProcessor<TRequest, TResponse>
    where TRequest : notnull
{
    public Task Process(TRequest request, TResponse response, CancellationToken cancellationToken)
        => audit.RecordAsync(typeof(TRequest).Name, response, cancellationToken);
}
```

`TResponse` is contravariant (`in`) — the post-processor consumes the response and cannot replace
it. If you need to *transform* the response, that is a behavior.

"Successfully" is load-bearing: if the handler throws, post-processors do not run. For cleanup that
must always happen, use a behavior with `try`/`finally`.

---

# Entry points

> Everything in this section describes intended behavior. There is no implementation yet.

## `ISender`

Sends requests. Inject this into callers that only send and never publish.

```csharp
public interface ISender
{
    Task<TResponse> Send<TResponse>(
        IRequest<TResponse> request, CancellationToken cancellationToken = default);

    Task<object?> Send(object request, CancellationToken cancellationToken = default);

    IAsyncEnumerable<TResponse> CreateStream<TResponse>(
        IStreamRequest<TResponse> request, CancellationToken cancellationToken = default);

    IAsyncEnumerable<object?> CreateStream(
        object request, CancellationToken cancellationToken = default);
}
```

```csharp
app.MapGet("/orders/{id:int}", async (int id, ISender sender, CancellationToken ct) =>
{
    var order = await sender.Send(new GetOrder(id), ct);
    return Results.Ok(order);
});
```

`TResponse` is inferred from the request, so `Send(new GetOrder(id))` returns `Task<Order>` with no
type argument needed.

Streaming:

```csharp
await foreach (var order in sender.CreateStream(new StreamOrdersForCustomer(42), ct))
{
    await WriteToResponse(order);
}
```

**The `object` overloads are an escape hatch.** They exist for dispatching a message deserialized at
runtime, where the type is not known at compile time:

```csharp
object request = JsonSerializer.Deserialize(payload, resolvedType)!;
var result = await sender.Send(request, ct);
```

They pay an extra reflection step to recover the response type, they box the response, and they
lose compile-time checking that a handler exists. Prefer the generic overloads everywhere else.

Prefer `ISender` over `IMediator` in application code — it is the narrower dependency and states
plainly what the class does.

## `IPublisher`

Publishes notifications.

```csharp
public interface IPublisher
{
    Task Publish(object notification, CancellationToken cancellationToken = default);

    Task Publish<TNotification>(
        TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification;
}
```

```csharp
public sealed class PlaceOrderHandler(IOrderRepository repository, IPublisher publisher)
    : IRequestHandler<PlaceOrder, PlaceOrderResult>
{
    public async Task<PlaceOrderResult> Handle(
        PlaceOrder request, CancellationToken cancellationToken)
    {
        var order = await repository.CreateAsync(request, cancellationToken).ConfigureAwait(false);

        await publisher
            .Publish(new OrderPlaced(order.Id, order.CustomerId, DateTimeOffset.UtcNow),
                     cancellationToken)
            .ConfigureAwait(false);

        return new PlaceOrderResult(order.Id);
    }
}
```

Awaiting `Publish` waits for the handlers to finish, under whichever
[INotificationPublisher](#inotificationpublisher) strategy is configured. This is in-process, not a
message bus — nothing is durable, and nothing survives the process. If you need delivery guarantees,
publish a notification whose handler enqueues to a real broker.

## `IMediator`

```csharp
public interface IMediator : ISender, IPublisher;
```

Both surfaces in one interface. Convenient at composition boundaries where a class genuinely does
both; otherwise inject `ISender` or `IPublisher` for the narrower dependency.

---

# Publishing strategy

## `INotificationPublisher`

Controls *how* notification handlers are invoked. Swap the implementation to change delivery
semantics application-wide.

```csharp
public interface INotificationPublisher
{
    Task Publish(
        IEnumerable<NotificationHandlerExecutor> handlerExecutors,
        INotification notification,
        CancellationToken cancellationToken);
}
```

Sequential — each handler completes before the next starts. The first exception stops the rest:

```csharp
public sealed class SequentialPublisher : INotificationPublisher
{
    public async Task Publish(
        IEnumerable<NotificationHandlerExecutor> handlerExecutors,
        INotification notification,
        CancellationToken cancellationToken)
    {
        foreach (var executor in handlerExecutors)
        {
            await executor.HandlerCallback(notification, cancellationToken).ConfigureAwait(false);
        }
    }
}
```

Parallel — all start together, all run to completion, exceptions aggregate:

```csharp
public sealed class ParallelPublisher : INotificationPublisher
{
    public Task Publish(
        IEnumerable<NotificationHandlerExecutor> handlerExecutors,
        INotification notification,
        CancellationToken cancellationToken)
        => Task.WhenAll(handlerExecutors.Select(
            executor => executor.HandlerCallback(notification, cancellationToken)));
}
```

The trade-off is real. Sequential is predictable and easy to debug, but one slow handler delays
every later one, and a throwing handler silently prevents them running at all. Parallel is faster
and isolates failures into an `AggregateException`, but handlers must be thread-safe and must not
share a scoped dependency that is not — an EF Core `DbContext`, for instance, will throw if two
handlers touch it concurrently.

Sequential is the safer default.

## `NotificationHandlerExecutor`

```csharp
public readonly record struct NotificationHandlerExecutor(
    object HandlerInstance,
    Func<INotification, CancellationToken, Task> HandlerCallback);
```

A resolved handler paired with a callback that invokes it with a correctly typed notification. The
engine builds these; you consume them when writing an `INotificationPublisher`.

`HandlerCallback` is the one to invoke — it closes over the cast from `INotification` to the
handler's concrete notification type. `HandlerInstance` is there for inspection: filtering,
ordering, or logging which handler is about to run.

```csharp
// Run handlers marked [Critical] first, then the rest in parallel.
var ordered = handlerExecutors
    .OrderByDescending(e => e.HandlerInstance
        .GetType()
        .IsDefined(typeof(CriticalAttribute), inherit: false));
```

It is a `readonly record struct`, so passing it around does not allocate.

---

# Values

## `Unit`

The response type of a request that returns nothing.

```csharp
public readonly struct Unit : IEquatable<Unit>, IComparable<Unit>, IComparable
{
    public static readonly Unit Value;
    public static Task<Unit> Task { get; }
}
```

.NET has no `Task<void>`, so a generic pipeline built on `Task<TResponse>` needs *some* type for
"nothing". `Unit` is that type: a struct with no fields, so it occupies no space and costs no
allocation. This is why `IRequest` is defined as `IRequest<Unit>` and why the whole engine has one
code path instead of two.

All `Unit` values are equal, and comparison always returns zero — there is only one value:

```csharp
Unit.Value == default   // true
Unit.Value.Equals(new Unit())   // true
```

**Use `Unit.Task` for the synchronous return, `Unit.Value` inside an `async` method:**

```csharp
public Task<Unit> Handle(CancelOrder request, CancellationToken ct)
{
    DoWorkSynchronously();
    return Unit.Task;              // cached instance, zero allocation
}

public async Task<Unit> Handle(CancelOrder request, CancellationToken ct)
{
    await DoWorkAsync(ct).ConfigureAwait(false);
    return Unit.Value;             // async machinery owns the task already
}
```

Never write `Task.FromResult(Unit.Value)` — it allocates a new `Task<Unit>` on every call for a
value that is always identical. `Unit.Task` is that same task, created once.

---

## Putting it together

The full path of one request through the system:

```csharp
// 1. The message
public sealed record PlaceOrder(int CustomerId, IReadOnlyList<int> ProductIds)
    : IRequest<PlaceOrderResult>;

// 2. Cross-cutting concerns, outermost first
public sealed class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull { /* ... */ }

public sealed class TransactionBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull { /* ... */ }

// 3. The one handler
public sealed class PlaceOrderHandler(IOrderRepository repository, IPublisher publisher)
    : IRequestHandler<PlaceOrder, PlaceOrderResult>
{
    public async Task<PlaceOrderResult> Handle(PlaceOrder request, CancellationToken ct)
    {
        var order = await repository.CreateAsync(request, ct).ConfigureAwait(false);
        await publisher.Publish(new OrderPlaced(order.Id, order.CustomerId, DateTimeOffset.UtcNow), ct)
                       .ConfigureAwait(false);
        return new PlaceOrderResult(order.Id);
    }
}

// 4. The fan-out
public sealed class SendConfirmationEmail : INotificationHandler<OrderPlaced> { /* ... */ }
public sealed class UpdateSalesMetrics   : INotificationHandler<OrderPlaced> { /* ... */ }

// 5. The caller
var result = await sender.Send(new PlaceOrder(customerId, productIds), ct);
```

Execution order:

```
Send(PlaceOrder)
  └─ LoggingBehavior          (before)
      └─ TransactionBehavior  (before — opens transaction)
          └─ PlaceOrderHandler
              └─ Publish(OrderPlaced)
                  ├─ SendConfirmationEmail
                  └─ UpdateSalesMetrics
          ── TransactionBehavior (after — commits)
      ── LoggingBehavior       (after — logs elapsed)
PlaceOrderResult returned
```

Note that the notification fan-out happens *inside* the transaction here, which is usually wrong —
handlers see uncommitted state, and an email goes out for an order that may still roll back. The
standard fix is to collect notifications during handling and publish them after commit. Worth
deciding deliberately when the engine is built.

---

## See also

- `CLAUDE.md` — architecture, hard rules, and current implementation status.
- `src/Mordecai/Abstractions/` — the contracts themselves, with XML docs.
- `tests/Mordecai.Tests/AbstractionsTests.cs` — executable assertions about this surface.
