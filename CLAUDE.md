# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What Mordecai is

A high-performance, in-process mediator for .NET, written from scratch and owned outright. It
implements the mediator pattern: requests routed to exactly one handler, notifications broadcast to
many, and a middleware pipeline wrapped around both.

The governing design goal is a fast send path with zero steady-state allocation: discovery happens
once at startup, and everything after that is a cached lookup and a virtual call.

**All code here is original.** Mordecai has no dependency on, and takes no code from, any other
mediator library. It uses standard mediator-pattern vocabulary — `IRequest`, `IRequestHandler`,
`INotification`, `IPipelineBehavior`, `ISender`, `IPublisher`, `IMediator`, `Unit` — because that
is the common language of the pattern and makes the API legible on sight. Names are the only thing
shared with anything else; every implementation decision is made here on its own merits. If you are
implementing a member, work from the semantics described in this file and in the XML docs, never by
consulting another library's source.

## Commands

```powershell
dotnet build Mordecai.slnx -c Release
dotnet test Mordecai.slnx -c Release
dotnet format Mordecai.slnx                      # run before committing
dotnet format Mordecai.slnx --verify-no-changes  # what CI checks

# One test project
dotnet test tests/Mordecai.Tests -c Release

# One test
dotnet test tests/Mordecai.Tests -c Release --filter "FullyQualifiedName~UnitIsZeroSized"

# Benchmarks -- Release only; BenchmarkDotNet refuses a Debug build
dotnet run -c Release --project benchmarks/Mordecai.Benchmarks -- --filter "*"
```

The solution file is `Mordecai.slnx`, not `.sln` — the .NET 10 SDK's XML solution format.
`dotnet build` with no argument also works.

## Layout

| Project | TFM | Role |
| --- | --- | --- |
| `src/Mordecai` | net10.0 | Public abstractions + runtime. The shipped package. |
| `src/Mordecai.SourceGenerator` | netstandard2.0 | Roslyn generator emitting the dispatch map. |
| `tests/Mordecai.Tests` | net10.0 | xunit v3. |
| `tests/Mordecai.Tests.Scanning` | net10.0 | Plain class library. A well-formed assembly for the scanner to walk. |
| `tests/Mordecai.SourceGenerator.Tests` | net10.0 | xunit v3, drives the generator via Roslyn. |
| `benchmarks/Mordecai.Benchmarks` | net10.0 | BenchmarkDotNet. |

`Mordecai.SourceGenerator` targets **netstandard2.0 and must stay there** — Roslyn loads analyzers
into the compiler process, which is netstandard2.0. This is not an oversight to "fix" during a
framework upgrade. Everything else targets net10.0.

### Supported .NET versions

**.NET 10 is the minimum, and every later .NET must work.** The package ships a single `net10.0`
build. NuGet selects it for any consumer on `net10.0` or later, so forward compatibility comes from
holding the floor, not from adding targets.

- **Do not raise the floor.** When a new .NET ships, `src/Mordecai` stays on `net10.0`. Raising it
  drops every .NET 10 consumer: a breaking change that needs an explicit decision and a major
  version, never a side effect of an SDK or framework upgrade.
- **Do not lower it.** Supporting .NET 8/9 was evaluated on 2026-09-25 and declined. They were one
  API swap away, but both leave Microsoft support on 2026-11-10.
- **No multi-targeting and no `#if` framework branching in `src/`.** An API that only exists in a
  newer runtime is not a reason to add a target; it waits until the floor moves.
- **Runtime dependencies must keep a `net10.0` asset.** Before bumping a runtime `PackageVersion`
  (today, `Microsoft.Extensions.DependencyInjection.Abstractions`) to a new major, confirm it still
  supports `net10.0`. Its unsupported-TFM warning is a build error here.
- **Test forward, not just at the floor.** Once a newer .NET is released, add it next to `net10.0`
  in the test projects' `TargetFrameworks` so the suite runs on the floor and the latest runtime.
  Multi-targeting the tests is fine; multi-targeting the shipped library is not.

Versions are centrally managed in `Directory.Packages.props`. Add a `PackageVersion` there and a
bare `PackageReference` (no `Version=`) in the project.

## Architecture

### Dispatch is reflection at startup, cached thereafter

Handler discovery is assembly scanning: `AddMordecai` reflects over the assemblies the caller names,
finds `IRequestHandler<,>` / `IRequestHandler<>` / `IStreamRequestHandler<,>` /
`INotificationHandler<>` implementations, and registers each with the container. The container owns
the registrations; Mordecai does not keep a parallel list.

This was a deliberate reversal of an earlier compile-time-generator design. The generator bought
Native AOT support, compile-time duplicate detection, and a few nanoseconds per send, at the cost of
a large amount of hard-to-debug machinery. Reflection plus caching is the simpler engine, and it is
needed regardless for runtime-loaded assemblies. **Do not reintroduce generated dispatch without
benchmarks justifying it.** If it comes back, it comes back as an optimization layered on this
implementation and validated by differential testing against it — same suite, both paths, identical
results.

`src/Mordecai.SourceGenerator` is retained as the home for that future work. It is **dormant**: the
generator emits nothing and its tests only prove the Roslyn harness runs. Do not wire it into
dispatch without an explicit decision to do so.

### The per-send lookup, and why it cannot be avoided

`ISender.Send<TResponse>(IRequest<TResponse> request)` gives you `TResponse` statically but **not**
the concrete request type — that is erased at the entry point. So each send must recover it at
runtime and invoke a wrapper that closes over `TRequest`. The alternative signature,
`Send<TRequest, TResponse>`, is rejected: C# will not partially infer type arguments, so every call
site would have to spell out both.

The consequence is a floor of one type lookup plus one virtual dispatch per send. Optimize within
that; do not chase designs that claim to eliminate it.

- **Cache the wrapper per request type.** `ConcurrentDictionary<Type, HandlerWrapper>` during
  warmup; consider swapping to `FrozenDictionary` once registration is sealed.
- **A static generic cache** (`Cache<TRequest>.Handler`, which the JIT folds to a constant address)
  *does* work for `IPublisher.Publish<TNotification>`, where the type parameter is real. It does not
  work for `Send`. An earlier version of this file claimed otherwise; it was wrong.
- **No locks on the send path.** Build once, read many.

### Do not use a key-value store for the handler map

The handler map is a few hundred entries, immutable after startup, read-only on the hot path. It is
not a database workload. Nothing in the FASTER / Tsavorite / Garnet family belongs here — those
solve durability and larger-than-memory datasets, and every feature they add (epoch protection,
hybrid log, sessions, checkpointing) is overhead against a dictionary probe.

### Trimming and AOT

Scanning is reflective, so the library is **not** Native AOT compatible, and the docs say so plainly
rather than implying otherwise.

Do not resolve this by turning `IsAotCompatible` off. Annotate the reflective entry points —
scanning methods and the `object` overloads of `Send` / `CreateStream` — with
`[RequiresUnreferencedCode]` and `[RequiresDynamicCode]`. Callers then get `IL2026` / `IL3050` at
their own call site naming the exact method, and the rest of the library stays under analysis.

### Public surface

`src/Mordecai/Abstractions/` holds the contracts and nothing else — no logic lives there.

- `IRequest<TResponse>` / `IRequest` (returns `Unit`) — one handler, enforced.
- `IStreamRequest<TResponse>` — one handler, returns `IAsyncEnumerable<TResponse>`.
- `INotification` — zero or more handlers, delivery order governed by `INotificationPublisher`.
- `IPipelineBehavior<TRequest, TResponse>` — middleware, outermost first in registration order.
  Not calling `next` short-circuits the pipeline.
- `ISender` / `IPublisher` / `IMediator` — the entry points consumers inject.

`Unit` is a zero-size readonly struct so a void-returning request costs no allocation, and
`Unit.Task` is a cached completed task. There is a test asserting both; keep it passing.

The public surface returns `Task<T>` rather than `ValueTask<T>` because handler results are
frequently awaited more than once by pipeline behaviors and cached in practice. Use `ValueTask`
freely for internal plumbing.

## Hard rules

**xunit is mandatory.** Every test in this repository is xunit (v3). Do not introduce NUnit,
MSTest, or any other framework, and do not add a test-only assertion or mocking library without
asking first. Every `src` project has a matching `tests` project; new public behavior ships with
tests in the same change.

**Performance is enforced, not aspirational.**

- New allocation on the send/publish hot path needs a benchmark showing it is free, or it does not land.
- `[MemoryDiagnoser]` stays on the benchmarks; the allocation column is the number that matters.
- `ConfigureAwait(false)` on every await in `src/` — this is library code.
- Shipping libraries set `IsAotCompatible`; trim and AOT analyzer warnings are build errors.

**`TreatWarningsAsErrors` is on repo-wide.** Fix the finding rather than suppressing it. The
existing suppressions in `.editorconfig` are public-API naming choices (`CA1711` on
`RequestHandlerDelegate`, `CA1716` on the `next` parameter) or tool constraints (`CA1822` in
benchmarks, since BenchmarkDotNet needs instance methods). Do not extend that list to silence
ordinary findings.

**`GenerateDocumentationFile` is on for shipping projects**, so every public member needs an XML
doc comment or the build fails.

## Documentation

- `README.md` — the repo's front door: what Mordecai is, quick start, headline benchmark numbers,
  and what it deliberately does not do. Keep the benchmark table in sync with `performance.md`.
- `docs/guides/abstractions.md` — every public type, with examples. Reflects what exists today.
- `docs/guides/usage.md` — **the contract for the engine.** The registration surface
  (`AddMordecai`, `MordecaiConfiguration`, `SequentialPublisher` / `ParallelPublisher`), lifetime
  and scoping semantics, and publisher behavior are all pinned down there. It described the engine
  before the engine existed and it still governs: change behavior and you change that page in the
  same commit, or fix the code instead. Do not let the two drift apart silently.
- `docs/guides/behaviors.md` — a catalogue of what to build with the pipeline, organised by the six
  levers a behavior has. Documents ordering guarantees and container constraint-filtering behavior
  that `PipelineBehaviorTests` pins; if you change either, change both.
- `docs/guides/performance.md` — benchmark results with the machine and runtime named, and the
  allocation accounting behind them. Re-run and update it when the dispatch path changes.

Code examples in all three guides are transcribed into a scratch project and compiled against the
real abstractions before the guide is published. Do the same when adding examples; a signature that
only looks right is how documentation rots.

## Current state

The engine is built and tested. In place:

- `Mediator` — `Send`, `Publish`, and `CreateStream`, both generic and `object` overloads, over a
  per-container cache of type-closed wrappers.
- `IPipelineBehavior<,>` and `IStreamPipelineBehavior<,>` composition, folded in reverse so the
  first-registered behavior is outermost. Open-generic and closed registrations share one chain.
- `SequentialPublisher` (default) and `ParallelPublisher`.
- DI registration — `AddMordecai`, `MordecaiConfiguration`, assembly scanning with deduplication
  by assembly identity and duplicate-handler detection at startup.
- Pre- and post-processors, implemented as built-in behaviors registered inside the user's own
  pipeline, and only when `AddOpenRequestPreProcessor` / `AddOpenRequestPostProcessor` was called.

Benchmarked in `docs/guides/performance.md`: **`Send` with no behaviors allocates 0 B** in steady
state and costs ~23 ns. Keep it that way.

Not implemented, deliberately:

- `MordecaiDispatchGenerator.Initialize` is an empty pipeline; it emits nothing, and by current
  design it stays that way. See the dispatch section above.
- Exception-handling behaviors are not declared.
- Open-generic *handler* types are rejected with `NotSupportedException` naming the type.
  Open-generic behaviors are fully supported.

`tests/Mordecai.Tests.Scanning` is a plain class library, not a test project: handler discovery is
per-assembly, and the scanning tests need a well-formed target separate from `Mordecai.Tests`,
which holds duplicate and open-generic handlers on purpose. Never add `Mordecai.Tests` itself to a
scan set.

Known rough edges are recorded in `docs/plans/OPEN-QUESTIONS.md`.

Use `tests/Mordecai.SourceGenerator.Tests/GeneratorHarnessTests.cs` (`GeneratorHarness.Run`) to
drive the generator through Roslyn when adding generator tests — do not build a real project to
test emission.
