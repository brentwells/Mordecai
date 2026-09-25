using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mordecai;
using Mordecai.Internal;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers Mordecai with a <see cref="IServiceCollection"/>.</summary>
public static class MordecaiServiceCollectionExtensions
{
    private const string ScanningJustification =
        "Handler discovery scans assemblies with reflection.";

    private const string DispatchJustification =
        "Handler dispatch constructs generic types at runtime.";

    /// <summary>
    /// Registers the mediator and the default notification publisher, and scans nothing.
    /// </summary>
    /// <remarks>
    /// With no assemblies configured there is nothing to discover, so this overload alone gives
    /// you a mediator that throws <see cref="HandlerNotFoundException"/> on the first send. It
    /// deliberately does not scan the calling assembly: implicit scanning makes what is registered
    /// depend on where the call happens to live.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    [RequiresUnreferencedCode(ScanningJustification)]
    [RequiresDynamicCode(DispatchJustification)]
    public static IServiceCollection AddMordecai(this IServiceCollection services)
        => services.AddMordecai(static _ => { });

    /// <summary>Registers the mediator and everything <paramref name="configure"/> asks for.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures assemblies to scan, behaviors, lifetime, and publisher.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="DuplicateHandlerException">
    /// Two implementations claim the same request.
    /// </exception>
    [RequiresUnreferencedCode(ScanningJustification)]
    [RequiresDynamicCode(DispatchJustification)]
    public static IServiceCollection AddMordecai(
        this IServiceCollection services,
        Action<MordecaiConfiguration> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var configuration = new MordecaiConfiguration();
        configure(configuration);

        var lifetime = configuration.Lifetime;

        services.AddSingleton(configuration.NotificationPublisher);
        services.TryAddSingleton<HandlerWrapperCache>();
        services.AddSingleton(new MordecaiRuntimeOptions
        {
            ThrowOnMissingHandler = configuration.ThrowOnMissingHandler,
        });

        // One Mediator registration; IMediator, ISender and IPublisher forward to it rather than
        // being three independent registrations that could drift apart.
        services.Add(new ServiceDescriptor(typeof(Mediator), typeof(Mediator), lifetime));
        services.Add(new ServiceDescriptor(
            typeof(IMediator), static sp => sp.GetRequiredService<Mediator>(), lifetime));
        services.Add(new ServiceDescriptor(
            typeof(ISender), static sp => sp.GetRequiredService<IMediator>(), lifetime));
        services.Add(new ServiceDescriptor(
            typeof(IPublisher), static sp => sp.GetRequiredService<IMediator>(), lifetime));

        HandlerScanner.Scan(services, configuration.AssembliesToScan, lifetime);

        AddPipelineRegistrations(services, configuration.Behaviors, lifetime);
        AddPipelineRegistrations(services, configuration.StreamBehaviors, lifetime);
        AddPipelineRegistrations(services, configuration.PreProcessors, lifetime);
        AddPipelineRegistrations(services, configuration.PostProcessors, lifetime);

        // Processors are behaviors underneath, added last so they land inside the user's own
        // pipeline: pre-processors immediately before the handler, post-processors immediately
        // after it returns. Only registered when there is something for them to run.
        if (configuration.PreProcessors.Count > 0)
        {
            services.Add(new ServiceDescriptor(
                typeof(IPipelineBehavior<,>), typeof(RequestPreProcessorBehavior<,>), lifetime));
        }

        if (configuration.PostProcessors.Count > 0)
        {
            services.Add(new ServiceDescriptor(
                typeof(IPipelineBehavior<,>), typeof(RequestPostProcessorBehavior<,>), lifetime));
        }

        return services;
    }

    [RequiresUnreferencedCode(ScanningJustification)]
    [RequiresDynamicCode(DispatchJustification)]
    private static void AddPipelineRegistrations(
        IServiceCollection services,
        IReadOnlyList<PipelineRegistration> registrations,
        ServiceLifetime defaultLifetime)
    {
        // Registration order is the pipeline order, so these go in exactly as written.
        foreach (var registration in registrations)
        {
            services.Add(new ServiceDescriptor(
                registration.ServiceType,
                registration.ImplementationType,
                registration.Lifetime ?? defaultLifetime));
        }
    }
}
