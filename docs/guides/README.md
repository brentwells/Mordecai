# Mordecai Guides

| Guide | What it covers | Status |
| --- | --- | --- |
| [Abstractions](abstractions.md) | Every public type in `namespace Mordecai`, what it does, and how to use it. | Implemented and tested. |
| [Usage](usage.md) | Installing, registering, and wiring each piece — DI, ASP.NET Core, testing, AOT. | Implemented and tested. |
| [Behaviors](behaviors.md) | A catalogue of middleware you can build with the pipeline, ordered by what each does to it. | Implemented and tested. |
| [Performance](performance.md) | Benchmark results for `Send` and `Publish`, with the allocation accounting. | Measured on the machine named in the file. |

Start with **Usage** to see how the pieces fit together, then **Abstractions** for the detail on any
individual type. **Behaviors** is the one to read when you know what a pipeline is and want to know
what to put in it; **Performance** when you want to know what the design bought.

All three are written against the real code in `src/Mordecai/`. Their examples are transcribed into
a scratch project and compiled before publication, so the signatures are correct rather than
plausible.

For architecture, hard rules, and current implementation status, see [CLAUDE.md](../../CLAUDE.md).
