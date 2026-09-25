using Microsoft.CodeAnalysis;

namespace Mordecai.SourceGenerator;

/// <summary>
/// Discovers <c>IRequestHandler</c>, <c>IStreamRequestHandler</c> and
/// <c>INotificationHandler</c> implementations in the compilation and emits a
/// closed-world dispatch map, so <c>Send</c> becomes a direct call rather than a
/// runtime type lookup.
/// </summary>
/// <remarks>
/// Scaffold only: the pipeline below emits nothing yet. See CLAUDE.md for the
/// intended emission shape and the invariants the generator must preserve.
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class MordecaiDispatchGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Registration of the handler-discovery pipeline goes here.
    }
}
