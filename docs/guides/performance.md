# Performance

Numbers, not adjectives. Mordecai's reason to exist is a fast send path with no steady-state
allocation, so the allocation column is the one that matters and it is checked at build time —
see [CLAUDE.md](../../CLAUDE.md) for the rules that protect it.

Reproduce with:

```powershell
dotnet run -c Release --project benchmarks/Mordecai.Benchmarks -- --filter "*"
```

---

## Results

```
BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8875/25H2/2025Update/HudsonValley2)
12th Gen Intel Core i9-12900HK 2.50GHz, 1 CPU, 20 logical and 14 physical cores
.NET SDK 10.0.301
  [Host]    : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3
  .NET 10.0 : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3

Job=.NET 10.0  Runtime=.NET 10.0
GC = Non-concurrent Workstation
```

| Method | Mean | Error | StdDev | Gen0 | Allocated |
| --- | ---: | ---: | ---: | ---: | ---: |
| `DirectHandlerCall` (baseline) | 0.21 ns | 0.047 ns | 0.042 ns | – | **0 B** |
| `SendWithoutBehaviors` | 23.66 ns | 0.392 ns | 0.677 ns | – | **0 B** |
| `SendThroughThreeBehaviors` | 83.63 ns | 1.709 ns | 3.038 ns | 0.0324 | 408 B |
| `SendWithTransientHandler` | 23.53 ns | 0.399 ns | 0.373 ns | 0.0019 | 24 B |
| `PublishSequentialToThreeHandlers` | 81.70 ns | 1.657 ns | 2.530 ns | 0.0293 | 368 B |
| `PublishParallelToThreeHandlers` | 155.03 ns | 2.739 ns | 2.562 ns | 0.0393 | 496 B |

Handlers are registered as singletons in every row except `SendWithTransientHandler`, so the
allocation column shows what Mordecai costs rather than what MS.DI charges to construct a handler.
The handler itself returns a cached `Task<string>`; a handler doing real work would drown out
everything being measured here.

**On reading the timings closely: don't.** Run-to-run drift on this laptop is around 5% — wider
than most of the differences anyone would want to reason about, and wider than any single change
made while building this. Two runs of identical code put `SendWithoutBehaviors` at 22.8 ns and
23.7 ns. The allocation column, by contrast, is exact and reproduced byte-for-byte across every
run; that is the number to hold the implementation to.

---

## Reading the numbers

### `Send` with no behaviors allocates nothing

**0 B, steady state.** That is the number the design exists to produce. What a send does after
warmup is one dictionary probe on `request.GetType()`, one virtual call into the cached wrapper,
one container lookup for the handler, and the handler's own call. No closure, no array, no boxing,
about 24 ns.

Getting the zero required one non-obvious thing. Resolving
`IEnumerable<IPipelineBehavior<TRequest, TResponse>>` from MS.DI allocates an array even when
nothing is registered, so a naive implementation pays for an empty pipeline on every send. The
wrapper instead remembers how many behaviors the container had for its request/response pair, on
the reasoning that registrations are fixed once the provider is built. A zero stays zero, and every
send after the first skips the resolution entirely.

### 22 ns of overhead, and why it cannot be zero

`ISender.Send<TResponse>(IRequest<TResponse>)` gives you the response type statically but **not**
the concrete request type — it is erased at the entry point. So every send has to recover the
request type at runtime and dispatch through a wrapper that closes over it. That is a floor of one
type lookup plus one virtual call, and no amount of tuning removes it; the alternative signature,
`Send<TRequest, TResponse>`, is rejected because C# will not partially infer type arguments and
every call site would have to spell out both.

The 0.21 ns baseline is a direct, inlineable call to a handler the JIT can see. It is there as a
floor, not as a target.

### Three behaviors cost 408 B, and that is the delegate chain

Exactly accounted for:

| | Count | Each | Total |
| --- | ---: | ---: | ---: |
| Closure holding the request and handler | 1 | 32 B | 32 B |
| Terminal `RequestHandlerDelegate` | 1 | 64 B | 64 B |
| Closure per behavior (behavior, continuation, parent) | 3 | 40 B | 120 B |
| Delegate per behavior | 3 | 64 B | 192 B |
| | | | **408 B** |

This is inherent to a delegate-based middleware pipeline: `next` is a `RequestHandlerDelegate<T>`,
it captures state, and captured state is an object. Nothing here is waste to be reclaimed — the
chain is built once per send and torn down with it. The array of resolved behaviors does *not*
appear, because MS.DI caches the resolved `IEnumerable` for singleton-lifetime services.

If a request is hot enough that 408 B matters, the lever is registering fewer behaviors for it —
constrain the behavior's `TRequest` to a marker interface so it does not apply at all, which costs
nothing at runtime because the constraint is resolved when the container closes the generic.

### The transient row is the realistic default

24 B, all of it MS.DI constructing a `GetOrderStatusHandler` per send. `Transient` is the default lifetime
and it is the right one for most handlers, so this is the number most applications will actually
see: the mediator's own overhead plus one handler allocation.

It comes out level with the singleton row (23.53 ns against 23.66 ns), and in one earlier run
marginally ahead of it. Read that as the two being indistinguishable on this machine, not as
transient resolution being cheaper than a cached lookup.

### Publishing allocates per handler

Notification delivery is not the path the zero-allocation rule protects, and it does allocate: an
executor array, plus a closure and a delegate per resolved handler, plus the publisher's async
state machine. `NotificationHandlerExecutor` pairs a handler with a
`Func<INotification, CancellationToken, Task>` callback, and that callback has to capture its
handler, so one delegate per handler per publish is structural rather than incidental.

`ParallelPublisher` costs 128 B and roughly 70 ns more than `SequentialPublisher` for the same
three handlers: a `List<Task>`, the `Task.WhenAll`, and an async wrapper per handler. That wrapper
is deliberate — it turns a handler that throws *before* returning a task into a faulted task, so
one badly behaved handler cannot starve the ones queued after it. Choose parallel when handler
latency actually matters, not to save allocations, and read
[the publisher comparison](usage.md#choosing-a-publisher-strategy) first: it is unsafe with a
shared scoped service.

Both figures include the guard that makes publishing correct when the call site has erased the
concrete type — a `GetType()` call and a reference comparison against `typeof(TNotification)`,
falling through to a cached wrapper when they disagree. Adding it moved the publish rows by less
than the run-to-run drift above and did not change the allocation column at all. See
[usage § 7](usage.md#7-notifications) for why it is not optional.

---

## What is not measured here

- **Startup.** Assembly scanning is reflection and scales with the number and size of assemblies
  named. It happens once, in `AddMordecai`. Name the assemblies that actually contain handlers.
- **First send per request type.** The first send of a given request type builds its wrapper via
  `Activator.CreateInstance` over a constructed generic. That cost is paid once per type for the
  life of the process and is warmed out of the benchmark deliberately, because it is a startup
  cost wearing a per-send disguise.
- **Streaming.** `CreateStream` returns the handler's own `IAsyncEnumerable` untouched when no
  stream behavior applies, so it adds a lookup and a virtual call to whatever the handler's async
  enumerator already costs. The interesting number there belongs to the handler, not the mediator.
