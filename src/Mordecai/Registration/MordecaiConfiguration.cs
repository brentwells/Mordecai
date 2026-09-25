using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Mordecai;

/// <summary>
/// Everything <c>AddMordecai</c> needs: which assemblies to scan for handlers, which behaviors to
/// wrap around them, and the lifetime and publisher strategy to use.
/// </summary>
/// <remarks>
/// Behaviors are never discovered by scanning — only handlers are. Behavior order is significant
/// and reflection's type order is not stable, so a scanned pipeline would order itself differently
/// between runs. Register them explicitly and the order is the order you wrote.
/// </remarks>
public sealed class MordecaiConfiguration
{
    private readonly HashSet<Assembly> _assembliesToScan = [];
    private readonly List<PipelineRegistration> _behaviors = [];
    private readonly List<PipelineRegistration> _streamBehaviors = [];
    private readonly List<PipelineRegistration> _preProcessors = [];
    private readonly List<PipelineRegistration> _postProcessors = [];

    /// <summary>
    /// Lifetime used for discovered handlers, the registered behaviors, and the mediator itself.
    /// Defaults to <see cref="ServiceLifetime.Transient"/>.
    /// </summary>
    public ServiceLifetime Lifetime { get; set; } = ServiceLifetime.Transient;

    /// <summary>
    /// Strategy used to invoke notification handlers. Defaults to <see cref="SequentialPublisher"/>.
    /// </summary>
    public INotificationPublisher NotificationPublisher { get; set; } = new SequentialPublisher();

    /// <summary>
    /// Whether <see cref="ISender.Send{TResponse}"/> throws <see cref="HandlerNotFoundException"/>
    /// when no handler is registered. Defaults to <see langword="true"/>; set it to
    /// <see langword="false"/> and an unhandled request yields the response type's default value.
    /// </summary>
    public bool ThrowOnMissingHandler { get; set; } = true;

    /// <summary>
    /// The accumulated set of assemblies to scan, deduplicated by assembly identity.
    /// </summary>
    public IReadOnlyCollection<Assembly> AssembliesToScan => _assembliesToScan;

    internal IReadOnlyList<PipelineRegistration> Behaviors => _behaviors;

    internal IReadOnlyList<PipelineRegistration> StreamBehaviors => _streamBehaviors;

    internal IReadOnlyList<PipelineRegistration> PreProcessors => _preProcessors;

    internal IReadOnlyList<PipelineRegistration> PostProcessors => _postProcessors;

    /// <summary>Adds one assembly to the scan set.</summary>
    /// <param name="assembly">The assembly to scan.</param>
    /// <returns>This configuration, for chaining.</returns>
    [RequiresUnreferencedCode(ScanningJustification)]
    [RequiresDynamicCode(DispatchJustification)]
    public MordecaiConfiguration RegisterServicesFromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        _assembliesToScan.Add(assembly);

        return this;
    }

    /// <summary>Adds the assembly containing <typeparamref name="T"/> to the scan set.</summary>
    /// <typeparam name="T">A type in the assembly to scan.</typeparam>
    /// <returns>This configuration, for chaining.</returns>
    [RequiresUnreferencedCode(ScanningJustification)]
    [RequiresDynamicCode(DispatchJustification)]
    public MordecaiConfiguration RegisterServicesFromAssemblyContaining<T>()
        => RegisterServicesFromAssemblyContaining(typeof(T));

    /// <summary>Adds the assembly containing <paramref name="type"/> to the scan set.</summary>
    /// <param name="type">A type in the assembly to scan.</param>
    /// <returns>This configuration, for chaining.</returns>
    [RequiresUnreferencedCode(ScanningJustification)]
    [RequiresDynamicCode(DispatchJustification)]
    public MordecaiConfiguration RegisterServicesFromAssemblyContaining(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return RegisterServicesFromAssembly(type.Assembly);
    }

    /// <summary>Adds several assemblies to the scan set.</summary>
    /// <param name="assemblies">The assemblies to scan.</param>
    /// <returns>This configuration, for chaining.</returns>
    [RequiresUnreferencedCode(ScanningJustification)]
    [RequiresDynamicCode(DispatchJustification)]
    public MordecaiConfiguration RegisterServicesFromAssemblies(params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        for (var i = 0; i < assemblies.Length; i++)
        {
            if (assemblies[i] is null)
            {
                throw new ArgumentNullException(
                    nameof(assemblies), $"The assembly at index {i} is null.");
            }

            _assembliesToScan.Add(assemblies[i]);
        }

        return this;
    }

    /// <summary>Adds a computed collection of assemblies to the scan set.</summary>
    /// <param name="assemblies">The assemblies to scan.</param>
    /// <returns>This configuration, for chaining.</returns>
    [RequiresUnreferencedCode(ScanningJustification)]
    [RequiresDynamicCode(DispatchJustification)]
    public MordecaiConfiguration RegisterServicesFromAssemblies(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        var index = 0;

        foreach (var assembly in assemblies)
        {
            if (assembly is null)
            {
                throw new ArgumentNullException(
                    nameof(assemblies), $"The assembly at index {index} is null.");
            }

            _assembliesToScan.Add(assembly);
            index++;
        }

        return this;
    }

    /// <summary>Adds the assemblies containing each marker type to the scan set.</summary>
    /// <param name="markerTypes">Types whose assemblies should be scanned.</param>
    /// <returns>This configuration, for chaining.</returns>
    [RequiresUnreferencedCode(ScanningJustification)]
    [RequiresDynamicCode(DispatchJustification)]
    public MordecaiConfiguration RegisterServicesFromAssembliesContaining(params Type[] markerTypes)
    {
        ArgumentNullException.ThrowIfNull(markerTypes);

        for (var i = 0; i < markerTypes.Length; i++)
        {
            if (markerTypes[i] is null)
            {
                throw new ArgumentNullException(
                    nameof(markerTypes), $"The marker type at index {i} is null.");
            }

            _assembliesToScan.Add(markerTypes[i].Assembly);
        }

        return this;
    }

    /// <summary>Adds the assemblies containing a computed collection of marker types.</summary>
    /// <param name="markerTypes">Types whose assemblies should be scanned.</param>
    /// <returns>This configuration, for chaining.</returns>
    [RequiresUnreferencedCode(ScanningJustification)]
    [RequiresDynamicCode(DispatchJustification)]
    public MordecaiConfiguration RegisterServicesFromAssembliesContaining(IEnumerable<Type> markerTypes)
    {
        ArgumentNullException.ThrowIfNull(markerTypes);

        var index = 0;

        foreach (var markerType in markerTypes)
        {
            if (markerType is null)
            {
                throw new ArgumentNullException(
                    nameof(markerTypes), $"The marker type at index {index} is null.");
            }

            _assembliesToScan.Add(markerType.Assembly);
            index++;
        }

        return this;
    }

    /// <summary>
    /// Registers an open-generic pipeline behavior — <c>typeof(LoggingBehavior&lt;,&gt;)</c> —
    /// that applies to every request whose type arguments satisfy its constraints.
    /// </summary>
    /// <param name="openBehaviorType">The open generic behavior type.</param>
    /// <param name="lifetime">Lifetime for this behavior; defaults to <see cref="Lifetime"/>.</param>
    /// <returns>This configuration, for chaining.</returns>
    public MordecaiConfiguration AddOpenBehavior(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] Type openBehaviorType,
        ServiceLifetime? lifetime = null)
        => AddOpenPipelineType(
            _behaviors, openBehaviorType, typeof(IPipelineBehavior<,>), lifetime, nameof(openBehaviorType));

    /// <summary>Registers a behavior for one closed request/response pair.</summary>
    /// <typeparam name="TService">The closed <see cref="IPipelineBehavior{TRequest, TResponse}"/>.</typeparam>
    /// <typeparam name="TImplementation">The behavior implementation.</typeparam>
    /// <param name="lifetime">Lifetime for this behavior; defaults to <see cref="Lifetime"/>.</param>
    /// <returns>This configuration, for chaining.</returns>
    public MordecaiConfiguration AddBehavior<TService,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TImplementation>(
        ServiceLifetime? lifetime = null)
        where TImplementation : TService
    {
        _behaviors.Add(new PipelineRegistration(typeof(TService), typeof(TImplementation), lifetime));

        return this;
    }

    /// <summary>Registers a behavior for one closed request/response pair.</summary>
    /// <param name="serviceType">The closed <see cref="IPipelineBehavior{TRequest, TResponse}"/>.</param>
    /// <param name="implementationType">The behavior implementation.</param>
    /// <param name="lifetime">Lifetime for this behavior; defaults to <see cref="Lifetime"/>.</param>
    /// <returns>This configuration, for chaining.</returns>
    public MordecaiConfiguration AddBehavior(
        Type serviceType,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type implementationType,
        ServiceLifetime? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        ArgumentNullException.ThrowIfNull(implementationType);

        _behaviors.Add(new PipelineRegistration(serviceType, implementationType, lifetime));

        return this;
    }

    /// <summary>Registers an open-generic behavior for stream requests.</summary>
    /// <param name="openBehaviorType">The open generic stream behavior type.</param>
    /// <param name="lifetime">Lifetime for this behavior; defaults to <see cref="Lifetime"/>.</param>
    /// <returns>This configuration, for chaining.</returns>
    public MordecaiConfiguration AddOpenStreamBehavior(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] Type openBehaviorType,
        ServiceLifetime? lifetime = null)
        => AddOpenPipelineType(
            _streamBehaviors,
            openBehaviorType,
            typeof(IStreamPipelineBehavior<,>),
            lifetime,
            nameof(openBehaviorType));

    /// <summary>Registers an open-generic pre-processor, run just before the handler.</summary>
    /// <param name="openPreProcessorType">The open generic pre-processor type.</param>
    /// <param name="lifetime">Lifetime for this processor; defaults to <see cref="Lifetime"/>.</param>
    /// <returns>This configuration, for chaining.</returns>
    public MordecaiConfiguration AddOpenRequestPreProcessor(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] Type openPreProcessorType,
        ServiceLifetime? lifetime = null)
        => AddOpenPipelineType(
            _preProcessors,
            openPreProcessorType,
            typeof(IRequestPreProcessor<>),
            lifetime,
            nameof(openPreProcessorType));

    /// <summary>Registers an open-generic post-processor, run after a successful handler return.</summary>
    /// <param name="openPostProcessorType">The open generic post-processor type.</param>
    /// <param name="lifetime">Lifetime for this processor; defaults to <see cref="Lifetime"/>.</param>
    /// <returns>This configuration, for chaining.</returns>
    public MordecaiConfiguration AddOpenRequestPostProcessor(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] Type openPostProcessorType,
        ServiceLifetime? lifetime = null)
        => AddOpenPipelineType(
            _postProcessors,
            openPostProcessorType,
            typeof(IRequestPostProcessor<,>),
            lifetime,
            nameof(openPostProcessorType));

    private MordecaiConfiguration AddOpenPipelineType(
        List<PipelineRegistration> target,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] Type openType,
        Type openInterface,
        ServiceLifetime? lifetime,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(openType, parameterName);

        if (!openType.IsGenericTypeDefinition)
        {
            var placeholder = new string(',', openInterface.GetGenericArguments().Length - 1);

            throw new ArgumentException(
                $"'{openType}' is not an open generic type. Pass the unbound form, " +
                $"for example typeof(YourType<{placeholder}>).",
                parameterName);
        }

        if (!ImplementsOpenInterface(openType, openInterface))
        {
            throw new ArgumentException(
                $"'{openType}' does not implement '{openInterface}'.", parameterName);
        }

        target.Add(new PipelineRegistration(openInterface, openType, lifetime));

        return this;
    }

    private static bool ImplementsOpenInterface(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] Type type,
        Type openInterface)
    {
        foreach (var implemented in type.GetInterfaces())
        {
            if (implemented.IsGenericType && implemented.GetGenericTypeDefinition() == openInterface)
            {
                return true;
            }
        }

        return false;
    }

    private const string ScanningJustification =
        "Handler discovery scans assemblies with reflection.";

    private const string DispatchJustification =
        "Handler dispatch constructs generic types at runtime.";
}

/// <summary>
/// A behavior or processor awaiting registration. Held rather than turned into a
/// <see cref="ServiceDescriptor"/> immediately, because <see cref="MordecaiConfiguration.Lifetime"/>
/// may still be assigned after the call that added it.
/// </summary>
internal readonly record struct PipelineRegistration(
    Type ServiceType,
    Type ImplementationType,
    ServiceLifetime? Lifetime);
