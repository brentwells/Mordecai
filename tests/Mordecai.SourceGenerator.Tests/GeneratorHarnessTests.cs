using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Mordecai.SourceGenerator.Tests;

/// <summary>
/// Drives <see cref="MordecaiDispatchGenerator"/> through Roslyn directly so tests can
/// assert on emitted source. Generator tests should always go through this harness
/// rather than building a real project.
/// </summary>
public static class GeneratorHarness
{
    public static GeneratorDriverRunResult Run(string source)
    {
        var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
            .Split(Path.PathSeparator)
            .Where(static path => !string.IsNullOrEmpty(path))
            .Select(static path => MetadataReference.CreateFromFile(path))
            .Cast<MetadataReference>()
            .Append(MetadataReference.CreateFromFile(typeof(IRequest<>).Assembly.Location))
            .ToImmutableArray();

        var compilation = CSharpCompilation.Create(
            assemblyName: "GeneratorTestAssembly",
            syntaxTrees: [CSharpSyntaxTree.ParseText(source)],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return CSharpGeneratorDriver
            .Create(new MordecaiDispatchGenerator())
            .RunGenerators(compilation)
            .GetRunResult();
    }
}

public sealed class GeneratorHarnessTests
{
    private const string HandlerSource = """
        using System.Threading;
        using System.Threading.Tasks;
        using Mordecai;

        namespace Sample;

        public sealed record GetOrderStatus(int OrderId) : IRequest<string>;

        public sealed class GetOrderStatusHandler : IRequestHandler<GetOrderStatus, string>
        {
            public Task<string> Handle(GetOrderStatus request, CancellationToken cancellationToken)
                => Task.FromResult("shipped");
        }
        """;

    [Fact]
    public void GeneratorRunsWithoutDiagnostics()
    {
        var result = GeneratorHarness.Run(HandlerSource);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void HarnessResolvesMordecaiAbstractions()
    {
        // Guards the harness itself: if the Mordecai reference were missing, the
        // compilation would silently fail to bind IRequest and every generator test
        // would pass vacuously.
        var result = GeneratorHarness.Run(HandlerSource);
        Assert.DoesNotContain(
            result.Results.SelectMany(static r => r.Diagnostics),
            static d => d.Severity == DiagnosticSeverity.Error);
    }
}
