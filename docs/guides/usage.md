# Using Mordecai

How to install, configure, and use every piece of the library.

This page describes what the library does today. `AddMordecai`, `Mediator`, pipeline composition,
streaming, and the pre/post processors are all implemented and covered by tests. For the numbers
behind the design, see [the performance guide](performance.md).

The one thing here that is not built is compile-time handler discovery for Native AOT; see
[Native AOT and trimming](#14-native-aot-and-trimming) for what that means in practice.

---

## Contents

1. [Install](#1-install)
2. [Quick start](#2-quick-start)
3. [Registration](#3-registration)
4. [Requests](#4-requests)
5. [Requests that return nothing](#5-requests-that-return-nothing)
6. [Streaming requests](#6-streaming-requests)
7. [Notifications](#7-notifications)
8. [Pipeline behaviors](#8-pipeline-behaviors)
9. [Pre- and post-processors](#9-pre--and-post-processors)
10. [Lifetimes and scoping](#10-lifetimes-and-scoping)
11. [ASP.NET Core](#11-aspnet-core)
12. [Background services](#12-background-services)
13. [Testing](#13-testing)
14. [Native AOT and trimming](#14-native-aot-and-trimming)
15. [Troubleshooting](#15-troubleshooting)

---

## 1. Install

Mordecai is not published to NuGet. Reference the project directly:

```powershell
dotnet add YourApp.csproj reference path\to\Mordecai\src\Mordecai\Mordecai.csproj
```

Once it ships, a single package reference is all you need:

```powershell
dotnet add package Mordecai
```

Your project must target `net10.0` or later. There is no downlevel support and none is planned; see
[CLAUDE.md](../../CLAUDE.md) for why.

---

## 2. Quick start

Five minutes, end to end.

**Define a request** — an immutable message describing what you want:

```csharp
using Mordecai;

public sealed record Order(int Id, string Status);

public sealed record GetOrder(int OrderId) : IRequest<Order>;
```

**Write its handler** — exactly one per request:

```csharp
public sealed class GetOrderHandler : IRequestHandler<GetOrder, Order>
{
    public Task<Order> Handle(GetOrder request, CancellationToken cancellationToken)
        => Task.FromResult(new Order(request.OrderId, "Shipped"));
}
```

**Register**

```csharp
builder.Services.AddMordecai();
```

**Send**

```csharp
public sealed class OrderLookup(ISender sender)
{
    public Task<Order> FindAsync(int orderId, CancellationToken ct)
        => sender.Send(new GetOrder(orderId), ct);
}
```

That is the whole loop. Everything below is refinement.

---

## 3. Registration

### What `AddMordecai()` does

```csharp
services.AddMordecai();
```

With no arguments it registers `IMediator`, `ISender`, and `IPublisher` — all resolving to the same
implementation — plus the default sequential notification publisher.

**It does not find any handlers.** With no assemblies configured there is nothing to scan, so this
overload alone gives you a mediator that throws `HandlerNotFoundException` on the first `Send`. You
almost always want to name at least one assembly:

```csharp
services.AddMordecai(cfg => cfg.RegisterServicesFromAssemblyContaining<Program>());
```

Handler discovery is **reflection at startup**: Mordecai scans the assemblies you name, finds the
types implementing the handler interfaces, and registers each one with the container. The container
holds the registrations — Mordecai does not maintain a parallel list of them.

What it does cache is the per-request-type *wrapper* needed to get from `IRequest<TResponse>` (all
`Send` knows statically) to the concrete `TRequest` the handler expects. Those are built on first
use and reused for the life of the process.

### Full configuration

```csharp
services.AddMordecai(cfg =>
{
    cfg.RegisterServicesFromAssemblyContaining<GetOrderHandler>();
    cfg.AddOpenBehavior(typeof(LoggingBehavior<,>));
    cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
    cfg.NotificationPublisher = new ParallelPublisher();
    cfg.Lifetime = ServiceLifetime.Scoped;
});
```

| Member | Default | Purpose |
| --- | --- | --- |
| `Lifetime` | `Transient` | Lifetime for handlers and the mediator itself. |
| `NotificationPublisher` | `SequentialPublisher` | How notification handlers are invoked. |
| `ThrowOnMissingHandler` | `true` | Whether a missing handler throws. Turn it off and `Send` returns the response type's default instead, and `CreateStream` returns an empty stream. |
| `RegisterServicesFromAssembly(asm)` | — | Scan one assembly. |
| `RegisterServicesFromAssemblyContaining<T>()` | — | Scan the assembly containing `T`. |
| `RegisterServicesFromAssemblyContaining(type)` | — | Non-generic form of the above. |
| `RegisterServicesFromAssemblies(params Assembly[])` | — | Scan several by assembly. |
| `RegisterServicesFromAssemblies(IEnumerable<Assembly>)` | — | Scan a computed collection. |
| `RegisterServicesFromAssembliesContaining(params Type[])` | — | Scan several by marker type. |
| `RegisterServicesFromAssembliesContaining(IEnumerable<Type>)` | — | Scan a computed collection of markers. |
| `AssembliesToScan` | empty | The accumulated, deduplicated set. Read-only. |
| `AddOpenBehavior(type, lifetime?)` | — | Register an open-generic behavior for all requests. |
| `AddBehavior<TService, TImpl>(lifetime?)` | — | Register a behavior for one closed request type. |
| `AddOpenStreamBehavior(type, lifetime?)` | — | Same, for stream requests. |
| `AddOpenRequestPreProcessor(type, lifetime?)` | — | Register an open-generic pre-processor. |
| `AddOpenRequestPostProcessor(type, lifetime?)` | — | Register an open-generic post-processor. |

The registration methods return the configuration object, so they chain:

```csharp
services.AddMordecai(cfg => cfg
    .RegisterServicesFromAssemblyContaining<GetOrderHandler>()
    .RegisterServicesFromAssembly(typeof(SomeOtherHandler).Assembly)
    .AddOpenBehavior(typeof(LoggingBehavior<,>), ServiceLifetime.Singleton));
```

### Scanning multiple assemblies

Handlers usually live across several projects. Every registration method has a plural form, and the
marker-type overloads are the ergonomic choice — you name a type you already have rather than
digging out an `Assembly`:

```csharp
services.AddMordecai(cfg => cfg.RegisterServicesFromAssembliesContaining(
    typeof(OrdersMarker),
    typeof(BillingMarker),
    typeof(ShippingMarker)));
```

Equivalent, if you already hold the assemblies:

```csharp
services.AddMordecai(cfg => cfg.RegisterServicesFromAssemblies(
    typeof(OrdersMarker).Assembly,
    typeof(BillingMarker).Assembly));
```

When the list is computed — a module registry, config-driven composition — pass any collection:

```csharp
List<Assembly> modules = [.. moduleRegistry.Select(m => m.Assembly)];

services.AddMordecai(cfg => cfg.RegisterServicesFromAssemblies(modules));
```

**Calls accumulate.** Mix and repeat them freely; each adds to the same set:

```csharp
services.AddMordecai(cfg =>
{
    cfg.RegisterServicesFromAssemblyContaining<Program>();
    cfg.RegisterServicesFromAssembliesContaining(typeof(OrdersMarker), typeof(BillingMarker));
    cfg.RegisterServicesFromAssemblies(typeof(ShippingMarker).Assembly);
});
```

#### Semantics you can rely on

**Assemblies are deduplicated.** Naming the same assembly twice — easy to do when three marker types
live in one project, or when a shared module gets registered by two callers — scans it once. This is
not a tidiness detail: without it, every `INotificationHandler` in that assembly would be registered
twice and **fire twice per publish**. Deduplication is by assembly identity, so
`RegisterServicesFromAssemblyContaining<OrdersMarker>()` and
`RegisterServicesFromAssembly(ordersAssembly)` collapse to one entry.

**Order does not matter.** Handler discovery is order-independent, and reflection does not guarantee
a stable type order anyway. Do not infer any execution order from registration order here.

**Behaviors are never discovered by scanning.** Only handlers are —
`IRequestHandler<,>`, `IRequestHandler<>`, `IStreamRequestHandler<,>`, `INotificationHandler<>`.
Pipeline behaviors must be registered explicitly with `AddOpenBehavior` / `AddBehavior`, precisely
*because* order matters for them and scan order is not deterministic. Discovering behaviors
automatically would make your pipeline order depend on reflection internals.

**Two handlers for one request is a startup error.** If scanning finds
`IRequestHandler<GetOrder, Order>` implemented in two assemblies, `AddMordecai` throws naming both
types. Notifications are exempt — many handlers is the entire point.

**A null entry throws.** `ArgumentNullException` naming the offending index, rather than a
`NullReferenceException` from somewhere inside the scan.

**Handlers are registered against the two-argument interface.** `IRequestHandler<CancelOrder>`
extends `IRequestHandler<CancelOrder, Unit>`, and scanning registers the two-argument form in both
cases so the dispatcher has one shape to look up. Resolve
`IRequestHandler<CancelOrder, Unit>` if you ever need the handler directly from the container.

**Open-generic handlers are not supported.** A type such as
`class Handler<T> : IRequestHandler<Query<T>, Result>` cannot be discovered, and scanning throws
`NotSupportedException` naming it rather than skipping it silently. Close the type arguments and
register concrete handlers. Note that this is a limitation on *handlers* only — open-generic
*behaviors* are fully supported and are the intended way to write something that applies across
many request types.

You can inspect the resolved set, which is useful in a test that asserts your composition root wired
up the modules you expected:

```csharp
var cfg = new MordecaiConfiguration();
cfg.RegisterServicesFromAssembliesContaining(typeof(OrdersMarker), typeof(BillingMarker));

Assert.Equal(2, cfg.AssembliesToScan.Count);
```

#### A note on `AppDomain.CurrentDomain.GetAssemblies()`

Deliberately not offered as a built-in. It returns only assemblies the CLR has *already loaded*, so
a module whose types have not been touched yet is silently missing — producing a
`HandlerNotFoundException` that appears in production and not in tests, and that moves depending on
what ran first. Name your assemblies explicitly. If you insist, the `IEnumerable<Assembly>` overload
accepts it and the failure mode is yours.

### The cost of scanning

Scanning is the normal path, but it is not free and it is worth knowing what you are buying.

- **Startup time** scales with the number and size of assemblies scanned. Name the assemblies that
  actually contain handlers rather than everything in the reference graph.
- **Trimming and Native AOT** cannot follow reflection. The scanning entry points are annotated
  `[RequiresUnreferencedCode]` and `[RequiresDynamicCode]`, so an AOT build warns at your call site
  naming the exact method rather than failing mysteriously at runtime. See
  [Native AOT](#14-native-aot-and-trimming).
- **Errors surface at startup, not compile time.** A missing handler is a `HandlerNotFoundException`
  the first time you send, and a duplicate handler throws while `AddMordecai` runs.

### Registering a behavior for one request type

```csharp
services.AddMordecai(cfg =>
    cfg.AddBehavior<IPipelineBehavior<GetOrder, Order>, GetOrderOnlyBehavior>());
```

Prefer constraining an open-generic behavior instead where you can — see
[targeting behaviors](#targeting-behaviors-with-constraints). Closed registration is for the case
where a behavior is genuinely bespoke to one request.

---

## 4. Requests

A request goes to exactly one handler and returns a value.

```csharp
public sealed record GetOrder(int OrderId) : IRequest<Order>;

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

Send it:

```csharp
var order = await sender.Send(new GetOrder(42), cancellationToken);
```

`TResponse` is inferred from the request type, so there is no type argument to supply and no cast on
the way out.

### Enforcing a CQRS split

Mordecai does not impose one. If you want commands and queries visibly separated, define your own
markers:

```csharp
public interface IQuery<out TResponse> : IRequest<TResponse>;
public interface ICommand<out TResponse> : IRequest<TResponse>;

public sealed record GetOrder(int OrderId) : IQuery<Order>;
public sealed record PlaceOrder(int CustomerId) : ICommand<PlaceOrderResult>;
```

This costs nothing and buys something real: a behavior can now constrain on `IQuery<>` and apply
only to reads. See [targeting behaviors](#targeting-behaviors-with-constraints).

### The weakly-typed overload

```csharp
object request = JsonSerializer.Deserialize(payload, resolvedType)!;
object? result = await sender.Send(request, cancellationToken);
```

This exists for dispatching messages whose type is not known until runtime. It pays an extra
reflection step to recover the response type from the request, it boxes the response, and it loses
the compile-time guarantee that a handler exists. Use the generic overload everywhere else.

---

## 5. Requests that return nothing

`IRequest` is shorthand for `IRequest<Unit>`:

```csharp
public sealed record CancelOrder(int OrderId) : IRequest;
```

The handler still returns `Task<Unit>` — there is no separate void path in the engine:

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

**In an `async` method return `Unit.Value`. In a synchronous one return `Unit.Task`:**

```csharp
public Task<Unit> Handle(CancelOrder request, CancellationToken cancellationToken)
{
    _log.Cancelled(request.OrderId);
    return Unit.Task;                        // cached; no allocation
}
```

Never write `Task.FromResult(Unit.Value)` — it allocates a fresh task per call for a value that is
always identical.

Calling it is the same as any other request; discard the result:

```csharp
await sender.Send(new CancelOrder(42), cancellationToken);
```

---

## 6. Streaming requests

For results produced incrementally — paging a large table, tailing a feed, relaying model tokens.

```csharp
public sealed record StreamOrders(int CustomerId) : IStreamRequest<Order>;
```

The handler returns `IAsyncEnumerable<T>` directly, **not** `async Task<...>`:

```csharp
using System.Runtime.CompilerServices;

public sealed class StreamOrdersHandler(IOrderRepository repository)
    : IStreamRequestHandler<StreamOrders, Order>
{
    public async IAsyncEnumerable<Order> Handle(
        StreamOrders request,
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

**`[EnumeratorCancellation]` is not optional.** Without it, a token the caller supplies via
`WithCancellation(...)` is silently dropped and your stream becomes uncancellable — a bug that
looks like a hang under load and nothing at all in tests.

Consume it:

```csharp
await foreach (var order in sender.CreateStream(new StreamOrders(42), cancellationToken))
{
    await WriteAsync(order);
}
```

If the result comfortably fits in memory, use `IRequest<IReadOnlyList<T>>` instead. A stream buys
you incremental delivery at the cost of async-enumerator machinery; do not pay it for a list of
fifty things.

---

## 7. Notifications

A notification is broadcast to every registered handler and returns nothing.

```csharp
public sealed record OrderPlaced(int OrderId, int CustomerId, DateTimeOffset PlacedAt)
    : INotification;
```

Handlers subscribe independently and do not know about each other:

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

Publish:

```csharp
await publisher.Publish(new OrderPlaced(order.Id, order.CustomerId, DateTimeOffset.UtcNow), ct);
```

**The static type at the call site does not matter.** Handlers are always resolved against the
notification's *runtime* type, so all of these reach the same handlers:

```csharp
await publisher.Publish(new OrderPlaced(1, 2, DateTimeOffset.UtcNow), ct);  // concrete
await publisher.Publish(evt, ct);              // evt declared as INotification, type erased
await Raise(publisher, evt, ct);               // erased again by the helper's type parameter

static Task Raise<T>(IPublisher publisher, T notification, CancellationToken ct)
    where T : INotification
    => publisher.Publish(notification, ct);
```

This matters because the alternative fails *silently*: resolving handlers from the inferred type
parameter would look for `INotificationHandler<INotification>` in the erased cases, find nothing,
and deliver to no one without an exception — zero handlers being a legal outcome. `Publish`
compares the type parameter to `notification.GetType()` and takes the runtime type when they
disagree. The comparison costs one reference check on a path that is not the one the
zero-allocation rule protects; see [performance](performance.md#publishing-allocates-per-handler).

Zero handlers is normal and not an error. If you need to know something was handled, you wanted a
request.

### Choosing a publisher strategy

```csharp
services.AddMordecai(cfg => cfg.NotificationPublisher = new ParallelPublisher());
```

| | `SequentialPublisher` (default) | `ParallelPublisher` |
| --- | --- | --- |
| Execution | One at a time, in order | All started together |
| First exception | Stops remaining handlers | Others still complete |
| Exception surfaced | Directly | `AggregateException` |
| Handler requirements | None | Must be thread-safe |
| Shared scoped services | Safe | **Unsafe** |

The last row is the one that bites. A scoped `DbContext` is not thread-safe; if two handlers touch
the same one under `ParallelPublisher`, EF Core throws
*"A second operation was started on this context instance before a previous operation completed."*
Either keep sequential, or give each handler its own scope.

Sequential is the default because it is predictable and debuggable. Move to parallel deliberately,
when handler latency actually matters and you have checked they are independent.

Writing your own is a small class — see
[`INotificationPublisher`](abstractions.md#inotificationpublisher).

### Ordering

Handlers have **no guaranteed order**, even under `SequentialPublisher`. Do not encode a dependency
between two handlers. If B must run after A, that is one handler doing two things, or A publishes a
second notification.

---

## 8. Pipeline behaviors

Middleware around request handling: logging, validation, transactions, caching, retries, auth.

```
Behavior A  ──►  Behavior B  ──►  Handler  ──►  B returns  ──►  A returns
```

### Writing one

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

        try
        {
            var response = await next(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("{Request} took {Elapsed}",
                name, Stopwatch.GetElapsedTime(start));
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

Register it:

```csharp
services.AddMordecai(cfg => cfg.AddOpenBehavior(typeof(LoggingBehavior<,>)));
```

### Ordering

Behaviors nest in registration order, **outermost first**:

```csharp
services.AddMordecai(cfg =>
{
    cfg.AddOpenBehavior(typeof(LoggingBehavior<,>));      // outermost
    cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
    cfg.AddOpenBehavior(typeof(TransactionBehavior<,>));  // innermost, closest to handler
});
```

Order matters more than it looks. Logging outermost captures validation failures. Transactions
innermost keeps the transaction as short as possible and keeps validation work outside it.

### Targeting behaviors with constraints

Constrain `TRequest` and the behavior applies only to matching requests. This is resolved at compile
time, so a behavior that does not apply costs nothing at runtime:

```csharp
// Only requests implementing your ICacheable marker.
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
            return cached;                    // handler never runs
        }

        var response = await next(cancellationToken).ConfigureAwait(false);
        cache.Set(request.CacheKey, response, request.CacheDuration);
        return response;
    }
}
```

Note the interface constraint is `notnull`, not `IRequest<TResponse>` — that is deliberate, and it
is what lets you constrain on your own markers instead.

### Short-circuiting

Not calling `next` stops the pipeline; the handler never runs. That is the mechanism behind caching
and authorization. **Call `next` exactly once** on any path that does not short-circuit — calling it
twice runs the handler twice.

### Always pass the token

```csharp
await next(cancellationToken).ConfigureAwait(false);   // correct
await next().ConfigureAwait(false);                    // compiles, silently drops cancellation
```

The parameter is optional so `next()` compiles. Passing it explicitly is also how you substitute a
modified token:

```csharp
using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
timeout.CancelAfter(TimeSpan.FromSeconds(30));

return await next(timeout.Token).ConfigureAwait(false);
```

### Stream behaviors

Stream requests use a parallel interface, `IStreamPipelineBehavior<,>`, registered with
`AddOpenStreamBehavior`. A regular behavior will not apply to a stream request. See
[the abstractions guide](abstractions.md#istreampipelinebehaviortrequest-tresponse).

### What to build with them

[The behaviors guide](behaviors.md) is a catalogue: validation, authorization, caching,
idempotency, transactions, retry, timeouts, throttling, tracing and exception mapping, each with a
worked example, plus a recommended ordering for the whole stack and the traps that come with it.

---

## 9. Pre- and post-processors

Narrower hooks than a behavior. A pre-processor runs before the handler:

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

A post-processor runs after it returns successfully:

```csharp
public sealed class AuditPostProcessor<TRequest, TResponse>(IAuditLog audit)
    : IRequestPostProcessor<TRequest, TResponse>
    where TRequest : notnull
{
    public Task Process(TRequest request, TResponse response, CancellationToken cancellationToken)
        => audit.RecordAsync(typeof(TRequest).Name, response, cancellationToken);
}
```

Register:

```csharp
services.AddMordecai(cfg => cfg
    .AddOpenRequestPreProcessor(typeof(ValidationPreProcessor<>))
    .AddOpenRequestPostProcessor(typeof(AuditPostProcessor<,>)));
```

Processors are ordinary pipeline behaviors underneath — there is no second mechanism — registered
*inside* your own behaviors. So a pre-processor is the last thing to run before the handler, a
post-processor the first thing after it returns, and both sit within any `try`/`finally` or
transaction a behavior of yours has opened. Neither behavior is registered at all unless you have
called `AddOpenRequestPreProcessor` or `AddOpenRequestPostProcessor`, so an application that uses
no processors pays nothing for them.

**Use a behavior instead when** you need to short-circuit, replace the response, wrap the call in
`try`/`finally`, or hold a resource across it. A processor can do none of those — its only levers
are mutating the request or throwing. That narrowness is the point.

Note "successfully": if the handler throws, post-processors do not run. For cleanup that must always
happen, use a behavior with `finally`.

---

## 10. Lifetimes and scoping

### Choosing a lifetime

```csharp
services.AddMordecai(cfg => cfg.Lifetime = ServiceLifetime.Scoped);
```

`Transient` is the default and is right for most handlers: cheap to construct, no shared state, no
surprises. Use `Scoped` when handlers should share a unit of work — one `DbContext` across every
handler in a web request. Avoid `Singleton` unless a handler is genuinely stateless and depends only
on singletons; a singleton handler that captures a scoped dependency is a captive-dependency bug.

The setting covers the mediator itself as well as your handlers, and that has one consequence worth
knowing. `IMediator`, `ISender`, and `IPublisher` are three forwarding registrations onto a single
`Mediator` registration — never three separate implementations — but "forwarding" only collapses to
*one instance* when the lifetime says instances are shared. Under `Scoped` or `Singleton`, resolving
all three inside a scope gives you the same object. Under the default `Transient`, each resolution
constructs its own, exactly as `Transient` promises. Nothing depends on the distinction: `Mediator`
holds no mutable state, and the wrapper caches it reads live in a container-wide singleton, so a
per-resolution mediator costs one small allocation and shares every cache.

### The captive dependency trap

The most common way to break a mediator: **a singleton resolving `ISender`**.

```csharp
// BROKEN: singleton captures the root provider.
public sealed class CacheWarmer(ISender sender) : IHostedService { }
```

The sender resolved here belongs to the root scope. When it dispatches to a handler that needs a
scoped `DbContext`, you get *"Cannot resolve scoped service from root provider"* — or worse, on some
configurations, a `DbContext` shared across every request in the process.

Inject `IServiceScopeFactory` and create a scope per operation. See
[background services](#12-background-services) for the pattern.

---

## 11. ASP.NET Core

### Minimal API

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMordecai(cfg =>
    cfg.RegisterServicesFromAssemblyContaining<Program>());

var app = builder.Build();

app.MapGet("/orders/{id:int}", async (
    int id,
    ISender sender,
    CancellationToken cancellationToken) =>
{
    var order = await sender.Send(new GetOrder(id), cancellationToken);
    return Results.Ok(order);
});

app.Run();
```

Take `CancellationToken` as a parameter — ASP.NET Core binds the request-aborted token, so a client
disconnect cancels the handler instead of letting it finish work nobody will read.

### Controllers

```csharp
[ApiController]
[Route("[controller]")]
public sealed class OrdersController(ISender sender) : ControllerBase
{
    [HttpGet("{id:int}")]
    public async Task<ActionResult<Order>> Get(int id, CancellationToken cancellationToken)
        => Ok(await sender.Send(new GetOrder(id), cancellationToken));
}
```

**Inject `ISender`, not `IMediator`.** A controller sends; it rarely publishes. The narrower
dependency documents intent and makes the class trivially fakeable in a test.

---

## 12. Background services

A `BackgroundService` is a singleton. It must create a scope per unit of work:

```csharp
public sealed class PollingService(IServiceScopeFactory scopeFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();

            await sender.Send(new PollForWork(), stoppingToken).ConfigureAwait(false);

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
        }
    }
}
```

Create the scope **inside** the loop, not outside. A scope held for the service's lifetime is a
singleton wearing a disguise, and any `DbContext` in it will accumulate tracked entities until the
process runs out of memory.

---

## 13. Testing

> **xunit is a hard rule in this repository.** Every test is xunit v3. Do not introduce NUnit,
> MSTest, or another framework. See [CLAUDE.md](../../CLAUDE.md).

### Test handlers directly

A handler is an ordinary class. It needs no mediator, no container, and no framework:

```csharp
public sealed class GetOrderHandlerTests
{
    [Fact]
    public async Task ReturnsOrderWhenFound()
    {
        var repository = new FakeOrderRepository { Orders = { [42] = new Order(42, 7) } };
        var handler = new GetOrderHandler(repository);

        var order = await handler.Handle(new GetOrder(42), TestContext.Current.CancellationToken);

        Assert.Equal(42, order.Id);
    }

    [Fact]
    public async Task ThrowsWhenMissing()
    {
        var handler = new GetOrderHandler(new FakeOrderRepository());

        await Assert.ThrowsAsync<OrderNotFoundException>(
            () => handler.Handle(new GetOrder(42), TestContext.Current.CancellationToken));
    }
}
```

`TestContext.Current.CancellationToken` is the xunit v3 idiom — it ties the token to the test's
timeout rather than passing `CancellationToken.None`.

This is the bulk of your testing, and it is the payoff for using a mediator: business logic sits in
small classes with explicit dependencies and no framework coupling.

### Test behaviors with a stub continuation

`RequestHandlerDelegate<T>` is just a delegate, so supply one inline:

```csharp
public sealed class CachingBehaviorTests
{
    [Fact]
    public async Task SkipsHandlerOnCacheHit()
    {
        var cache = new FakeCache();
        cache.Set("order-status:42", "cached", TimeSpan.FromMinutes(1));

        var handlerRan = false;
        RequestHandlerDelegate<string> next = _ =>
        {
            handlerRan = true;
            return Task.FromResult("fresh");
        };

        var behavior = new CachingBehavior<GetOrderStatus, string>(cache);
        var result = await behavior.Handle(
            new GetOrderStatus(42), next, TestContext.Current.CancellationToken);

        Assert.Equal("cached", result);
        Assert.False(handlerRan);
    }
}
```

Asserting the continuation did *not* run is the whole point of a short-circuit test — checking only
the return value would pass even if the handler ran and its result was discarded.

### Fake `ISender` for classes that depend on it

```csharp
public sealed class StubSender : ISender
{
    public Task<TResponse> Send<TResponse>(
        IRequest<TResponse> request, CancellationToken cancellationToken = default)
        => Task.FromResult(default(TResponse)!);

    public Task<object?> Send(object request, CancellationToken cancellationToken = default)
        => Task.FromResult<object?>(null);

    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
        IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
        => AsyncEnumerable.Empty<TResponse>();

    public IAsyncEnumerable<object?> CreateStream(
        object request, CancellationToken cancellationToken = default)
        => AsyncEnumerable.Empty<object?>();
}
```

`AsyncEnumerable.Empty<T>()` is built into .NET 10; no helper package needed.

### Integration tests through the real container

Use these to verify wiring — registration, behavior ordering, publisher strategy — not business
logic:

```csharp
[Fact]
public async Task PipelineRunsInRegisteredOrder()
{
    var services = new ServiceCollection();
    services.AddSingleton<IOrderRepository, FakeOrderRepository>();
    services.AddMordecai(cfg =>
    {
        cfg.RegisterServicesFromAssemblyContaining<GetOrderHandler>();
        cfg.AddOpenBehavior(typeof(RecordingBehavior<,>));
    });

    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();

    var sender = scope.ServiceProvider.GetRequiredService<ISender>();
    var order = await sender.Send(new GetOrder(42), TestContext.Current.CancellationToken);

    Assert.Equal(42, order.Id);
}
```

Always resolve from a scope, never the root provider — the root cannot supply scoped services, and a
test that resolves from it will pass while production fails.

---

## 14. Native AOT and trimming

**Assembly scanning is not AOT-compatible, and Mordecai's handler discovery is assembly scanning.**
Be clear-eyed about this: the reflection-based engine trades AOT support for a dramatically simpler
implementation. If you need Native AOT today, Mordecai is not yet the right choice.

What the library does instead of pretending otherwise: the scanning entry points carry

```csharp
[RequiresUnreferencedCode("Handler discovery scans assemblies with reflection.")]
[RequiresDynamicCode("Handler dispatch constructs generic types at runtime.")]
```

so an AOT or trimmed build produces `IL2026` / `IL3050` **at your call site**, naming the exact
method. You find out at build time, not from a `MissingMethodException` in production.

The `object` overloads — `Send(object)` and `CreateStream(object)` — carry the same annotations for
the same reason.

Everything else in the library is analyzable and stays clean, which is why the annotations are
scoped to the reflective methods rather than switching `IsAotCompatible` off wholesale.

Full AOT support requires compile-time handler discovery — a source generator emitting explicit
registrations. `src/Mordecai.SourceGenerator` exists as the home for that work but is dormant; see
[CLAUDE.md](../../CLAUDE.md) for status.

---

## 15. Troubleshooting

| Symptom | Cause | Fix |
| --- | --- | --- |
| `HandlerNotFoundException` on first send | Handler's assembly was never named for scanning | Add it via `RegisterServicesFromAssembliesContaining(...)` |
| Handler found locally, missing in production | Relied on `AppDomain.CurrentDomain.GetAssemblies()` | Name assemblies explicitly — the module was simply not loaded yet |
| Duplicate handler exception at startup | Same `IRequestHandler<T, R>` in two scanned assemblies | Delete one, or stop scanning the assembly you did not mean to include |
| Notification handler fires twice | Same assembly registered twice through different overloads | Should not happen — dedup is by assembly identity. File a bug |
| Notification handler never fires | Not registered, or type is not `INotification` | Confirm the marker interface and the registration source |
| `NotSupportedException` naming a handler at startup | The handler is an open generic type | Close the type arguments; open-generic *behaviors* are supported, open-generic handlers are not |
| Pre- or post-processor never runs | `AddOpenRequestPreProcessor` / `AddOpenRequestPostProcessor` was never called | Registering the processor type with the container directly is not enough — the built-in behavior that invokes them is only added by those methods |
| `A second operation was started on this context` | `ParallelPublisher` + shared scoped `DbContext` | Switch to `SequentialPublisher`, or scope per handler |
| `Cannot resolve scoped service from root provider` | Singleton injected `ISender` | Inject `IServiceScopeFactory`, scope per operation |
| Stream never cancels | Missing `[EnumeratorCancellation]` | Add it to the handler's token parameter |
| Handler runs twice | Behavior called `next` more than once | Call it exactly once |
| Cancellation ignored mid-pipeline | Behavior called `next()` with no token | Pass `cancellationToken` explicitly |
| Trim/AOT warnings after publish | Assembly scanning or `Send(object)` | Remove them, or root the types manually |

---

## See also

- [Abstractions guide](abstractions.md) — every public type, in detail.
- [CLAUDE.md](../../CLAUDE.md) — architecture, hard rules, implementation status.
- `src/Mordecai/Abstractions/` — the contracts, with XML docs.
