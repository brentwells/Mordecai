# Mordecai

A high-performance, in-process mediator for .NET. Requests go to exactly one handler,
notifications broadcast to many, and a middleware pipeline wraps around both.

The governing design goal is a fast send path with **zero steady-state allocation**: discovery
happens once at startup, and everything after that is a cached lookup and a virtual call.

> **Status:** the engine is implemented, tested, and benchmarked, and
> [published on NuGet](https://www.nuget.org/packages/Mordecai). Targets `net10.0`; runs on .NET 10 and later.

---

## Quick start

Install:

```powershell
dotnet add package Mordecai
```

Define a request:

```csharp
using Mordecai;

public sealed record Order(int Id, string Status);

public sealed record GetOrder(int OrderId) : IRequest<Order>;
```

Write its handler — exactly one per request:

```csharp
public sealed class GetOrderHandler : IRequestHandler<GetOrder, Order>
{
    public Task<Order> Handle(GetOrder request, CancellationToken cancellationToken)
        => Task.FromResult(new Order(request.OrderId, "Shipped"));
}
```

Register, naming the assemblies that contain your handlers:

```csharp
builder.Services.AddMordecai(cfg =>
    cfg.RegisterServicesFromAssemblyContaining<Program>());
```

Send:

```csharp
public sealed class OrderLookup(ISender sender)
{
    public Task<Order> FindAsync(int orderId, CancellationToken ct)
        => sender.Send(new GetOrder(orderId), ct);
}
```

That is the whole loop. [The usage guide](docs/guides/usage.md) covers the rest.

---

## Performance

From [`docs/guides/performance.md`](docs/guides/performance.md), which has the machine, the runtime
version, and the byte-by-byte accounting:

| | Mean | Allocated |
| --- | ---: | ---: |
| Direct handler call (baseline) | 0.21 ns | 0 B |
| **`Send`, no behaviors** | **23.66 ns** | **0 B** |
| `Send` through three behaviors | 83.63 ns | 408 B |

The zero is the point, and it is enforced rather than hoped for: new allocation on the send or
publish path needs a benchmark showing it is free, or it does not land.

The ~24 ns is a floor, not a shortfall. `ISender.Send<TResponse>(IRequest<TResponse>)` gives you the
response type statically but not the concrete request type — that is erased at the entry point — so
every send has to recover it at runtime and dispatch through a wrapper that closes over it. One type
lookup, one virtual call. The alternative signature, `Send<TRequest, TResponse>`, is rejected
because C# will not partially infer type arguments and every call site would have to spell out both.

---

## What's in the box

- **Requests** — `IRequest<TResponse>`, or `IRequest` for the void case, which returns a zero-size
  `Unit` so nothing is allocated for a response nobody reads.
- **Streaming** — `IStreamRequest<T>` handlers returning `IAsyncEnumerable<T>`, with cancellation
  wired correctly through `[EnumeratorCancellation]`.
- **Notifications** — many handlers per event, delivered by a swappable `INotificationPublisher`.
  `SequentialPublisher` is the default; `ParallelPublisher` starts everything at once and aggregates
  the failures.
- **Pipeline behaviors** — `IPipelineBehavior<,>` and `IStreamPipelineBehavior<,>`, nesting in
  registration order with the first registered outermost. Open-generic and closed registrations
  share one ordered chain.
- **Pre- and post-processors** — narrower hooks than a behavior, run inside your own pipeline.
- **Registration** — assembly scanning with deduplication by assembly identity, and duplicate
  handlers caught at startup rather than at the first send.

## What it deliberately does not do

- **Native AOT.** Handler discovery is assembly scanning, which is reflection, and the docs say so
  plainly instead of implying otherwise. The reflective entry points carry
  `[RequiresUnreferencedCode]` and `[RequiresDynamicCode]`, so a trimmed or AOT build warns at your
  own call site naming the exact method. See [usage § 14](docs/guides/usage.md#14-native-aot-and-trimming).
- **Open-generic handler types.** Scanning rejects them with a `NotSupportedException` naming the
  type. Open-generic *behaviors* are fully supported and are the intended way to write something
  that applies across many request types.
- **Behavior discovery by scanning.** Order matters for behaviors and reflection's type order is not
  stable, so they are registered explicitly. A scanned pipeline would order itself differently
  between runs.
- **Exception-handling behaviors.** Not declared yet.

---

## Originality

**All code here is original.** Mordecai has no dependency on, and takes no code from, any other
mediator library. It uses standard mediator-pattern vocabulary — `IRequest`, `IRequestHandler`,
`INotification`, `IPipelineBehavior`, `ISender`, `IPublisher`, `IMediator`, `Unit` — because that is
the common language of the pattern and makes the API legible on sight. Names are the only thing
shared with anything else; every implementation decision is made here on its own merits.

---

## Repository layout

| Project | Role |
| --- | --- |
| `src/Mordecai` | Public abstractions and runtime. The shipped package. |
| `src/Mordecai.SourceGenerator` | Roslyn generator, **dormant** — the home for future compile-time dispatch. Emits nothing today. |
| `tests/Mordecai.Tests` | xunit v3. |
| `tests/Mordecai.Tests.Scanning` | A plain class library, present so the scanner has a well-formed assembly to walk. |
| `benchmarks/Mordecai.Benchmarks` | BenchmarkDotNet. |

The solution file is `Mordecai.slnx`, the .NET 10 SDK's XML format — not `.sln`.

## Building

```powershell
dotnet build Mordecai.slnx -c Release
dotnet test Mordecai.slnx -c Release
dotnet format Mordecai.slnx --verify-no-changes

# Benchmarks are Release-only; BenchmarkDotNet refuses a Debug build.
dotnet run -c Release --project benchmarks/Mordecai.Benchmarks -- --filter "*"
```

`TreatWarningsAsErrors` is on repo-wide, and the shipping library opts into trim and AOT analysis,
so those warnings are build errors too. Fix the finding rather than suppressing it.

---

## Documentation

- **[Usage](docs/guides/usage.md)** — installing, registering, and wiring each piece: DI, ASP.NET
  Core, background services, testing, AOT, troubleshooting.
- **[Abstractions](docs/guides/abstractions.md)** — every public type, in detail.
- **[Behaviors](docs/guides/behaviors.md)** — a catalogue of middleware to build with the pipeline:
  validation, caching, transactions, retry, timeouts, tracing, and where each belongs in the chain.
- **[Performance](docs/guides/performance.md)** — benchmark results and where each byte goes.
- **[CLAUDE.md](CLAUDE.md)** — architecture, hard rules, and implementation status.

## License

[MIT](LICENSE).
