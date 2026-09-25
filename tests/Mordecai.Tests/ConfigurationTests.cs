using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Mordecai.Tests.Scanning;

namespace Mordecai.Tests;

public sealed class ConfigurationTests
{
    private static readonly Assembly ScanTarget = typeof(ScanTargetMarker).Assembly;
    private static readonly Assembly Library = typeof(Unit).Assembly;

    [Fact]
    public void DefaultsMatchTheDocumentedContract()
    {
        var configuration = new MordecaiConfiguration();

        Assert.Equal(ServiceLifetime.Transient, configuration.Lifetime);
        Assert.IsType<SequentialPublisher>(configuration.NotificationPublisher);
        Assert.True(configuration.ThrowOnMissingHandler);
        Assert.Empty(configuration.AssembliesToScan);
    }

    [Fact]
    public void EveryRegistrationOverloadAddsItsAssembly()
    {
        // Held in a local so CA2263 does not push the non-generic overload towards its generic
        // sibling; the point here is that both overloads exist and behave identically.
        var marker = typeof(ScanTargetMarker);

        Assert.Equal([ScanTarget], new MordecaiConfiguration()
            .RegisterServicesFromAssembly(ScanTarget).AssembliesToScan);

        Assert.Equal([ScanTarget], new MordecaiConfiguration()
            .RegisterServicesFromAssemblyContaining<ScanTargetMarker>().AssembliesToScan);

        Assert.Equal([ScanTarget], new MordecaiConfiguration()
            .RegisterServicesFromAssemblyContaining(marker).AssembliesToScan);

        Assert.Equal([ScanTarget], new MordecaiConfiguration()
            .RegisterServicesFromAssemblies(ScanTarget).AssembliesToScan);

        Assert.Equal([ScanTarget], new MordecaiConfiguration()
            .RegisterServicesFromAssemblies(new List<Assembly> { ScanTarget }).AssembliesToScan);

        Assert.Equal([ScanTarget], new MordecaiConfiguration()
            .RegisterServicesFromAssembliesContaining(typeof(ScanTargetMarker)).AssembliesToScan);

        Assert.Equal([ScanTarget], new MordecaiConfiguration()
            .RegisterServicesFromAssembliesContaining(new List<Type> { typeof(ScanTargetMarker) })
            .AssembliesToScan);
    }

    [Fact]
    public void CallsAccumulateAcrossOverloads()
    {
        var configuration = new MordecaiConfiguration()
            .RegisterServicesFromAssemblyContaining<ScanTargetMarker>()
            .RegisterServicesFromAssembly(Library);

        Assert.Equal(2, configuration.AssembliesToScan.Count);
        Assert.Contains(ScanTarget, configuration.AssembliesToScan);
        Assert.Contains(Library, configuration.AssembliesToScan);
    }

    [Fact]
    public void OneAssemblyReachedThroughTwoOverloadsIsOneEntry()
    {
        var configuration = new MordecaiConfiguration()
            .RegisterServicesFromAssemblyContaining<ScanTargetMarker>()
            .RegisterServicesFromAssembly(ScanTarget);

        Assert.Single(configuration.AssembliesToScan);
    }

    [Fact]
    public void ThreeMarkerTypesFromOneAssemblyAreOneEntry()
    {
        var configuration = new MordecaiConfiguration()
            .RegisterServicesFromAssembliesContaining(
                typeof(ScanTargetMarker),
                typeof(OtherScanTargetMarker),
                typeof(Ordered));

        Assert.Single(configuration.AssembliesToScan);
    }

    [Fact]
    public void NullAssemblyThrows()
    {
        Assert.Throws<ArgumentNullException>(
            () => new MordecaiConfiguration().RegisterServicesFromAssembly(null!));
    }

    [Fact]
    public void NullMarkerTypeThrows()
    {
        Assert.Throws<ArgumentNullException>(
            () => new MordecaiConfiguration().RegisterServicesFromAssemblyContaining((Type)null!));
    }

    [Fact]
    public void NullArrayThrows()
    {
        Assert.Throws<ArgumentNullException>(
            () => new MordecaiConfiguration().RegisterServicesFromAssemblies((Assembly[])null!));

        Assert.Throws<ArgumentNullException>(
            () => new MordecaiConfiguration().RegisterServicesFromAssemblies((IEnumerable<Assembly>)null!));

        Assert.Throws<ArgumentNullException>(
            () => new MordecaiConfiguration().RegisterServicesFromAssembliesContaining((Type[])null!));

        Assert.Throws<ArgumentNullException>(
            () => new MordecaiConfiguration().RegisterServicesFromAssembliesContaining((IEnumerable<Type>)null!));
    }

    [Fact]
    public void NullArrayElementThrowsNamingTheIndex()
    {
        var assemblies = Assert.Throws<ArgumentNullException>(
            () => new MordecaiConfiguration().RegisterServicesFromAssemblies(ScanTarget, null!));

        Assert.Contains("index 1", assemblies.Message, StringComparison.Ordinal);

        var markers = Assert.Throws<ArgumentNullException>(
            () => new MordecaiConfiguration()
                .RegisterServicesFromAssembliesContaining(typeof(ScanTargetMarker), null!));

        Assert.Contains("index 1", markers.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NullElementInAComputedCollectionThrowsNamingTheIndex()
    {
        var assemblies = Assert.Throws<ArgumentNullException>(
            () => new MordecaiConfiguration()
                .RegisterServicesFromAssemblies(new List<Assembly> { ScanTarget, null! }));

        Assert.Contains("index 1", assemblies.Message, StringComparison.Ordinal);

        var markers = Assert.Throws<ArgumentNullException>(
            () => new MordecaiConfiguration()
                .RegisterServicesFromAssembliesContaining(new List<Type> { typeof(Unit), null! }));

        Assert.Contains("index 1", markers.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddOpenBehaviorRejectsAClosedType()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new MordecaiConfiguration().AddOpenBehavior(typeof(ClosedBehavior)));

        Assert.Contains("open generic", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddOpenBehaviorRejectsATypeThatIsNotABehavior()
    {
        Assert.Throws<ArgumentException>(
            () => new MordecaiConfiguration().AddOpenBehavior(typeof(NotABehavior<,>)));
    }

    [Fact]
    public void AddOpenStreamBehaviorRejectsARequestBehavior()
    {
        Assert.Throws<ArgumentException>(
            () => new MordecaiConfiguration().AddOpenStreamBehavior(typeof(NotABehavior<,>)));
    }

    private sealed class ClosedBehavior : IPipelineBehavior<Fakes.Echo, string>
    {
        public Task<string> Handle(
            Fakes.Echo request,
            RequestHandlerDelegate<string> next,
            CancellationToken cancellationToken)
            => next(cancellationToken);
    }

    private sealed class NotABehavior<TRequest, TResponse>;
}
