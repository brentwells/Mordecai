using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Mordecai.Internal;

/// <summary>
/// Finds handler implementations in the configured assemblies and registers each against its
/// closed handler interface. This is the whole of Mordecai's discovery: the container ends up
/// owning the registrations, and no parallel list is kept.
/// </summary>
internal static class HandlerScanner
{
    /// <summary>
    /// Registers every handler found in <paramref name="assemblies"/> with
    /// <paramref name="services"/>.
    /// </summary>
    /// <exception cref="DuplicateHandlerException">
    /// Two implementations claim the same request or stream request.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// A handler is an open generic type, which is not supported.
    /// </exception>
    [RequiresUnreferencedCode("Handler discovery scans assemblies with reflection.")]
    [RequiresDynamicCode("Handler dispatch constructs generic types at runtime.")]
    public static void Scan(
        IServiceCollection services,
        IEnumerable<Assembly> assemblies,
        ServiceLifetime lifetime)
    {
        // Closed handler interface -> the implementation that claimed it. Only requests and
        // stream requests go in here; notifications are meant to have many handlers. It spans
        // every assembly, so a request handled in two of them is still caught.
        var exclusiveHandlers = new Dictionary<Type, Type>();

        foreach (var assembly in assemblies)
        {
            ScanTypes(services, TypesOf(assembly), lifetime, exclusiveHandlers);
        }
    }

    /// <summary>
    /// The per-type half of <see cref="Scan"/>, split out so tests can drive it with a handful of
    /// deliberately malformed types without needing a whole assembly built around them.
    /// </summary>
    [RequiresUnreferencedCode("Handler discovery inspects types with reflection.")]
    [RequiresDynamicCode("Handler dispatch constructs generic types at runtime.")]
    internal static void ScanTypes(
        IServiceCollection services,
        IEnumerable<Type> types,
        ServiceLifetime lifetime,
        Dictionary<Type, Type> exclusiveHandlers)
    {
        foreach (var type in types)
        {
            if (!type.IsClass || type.IsAbstract)
            {
                continue;
            }

            if (type.ContainsGenericParameters)
            {
                GuardAgainstOpenGenericHandler(type);
                continue;
            }

            RegisterHandlerInterfaces(services, type, lifetime, exclusiveHandlers);
        }
    }

    private static void RegisterHandlerInterfaces(
        IServiceCollection services,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type implementation,
        ServiceLifetime lifetime,
        Dictionary<Type, Type> exclusiveHandlers)
    {
        foreach (var implemented in implementation.GetInterfaces())
        {
            if (!implemented.IsGenericType)
            {
                continue;
            }

            var definition = implemented.GetGenericTypeDefinition();

            if (definition == typeof(IRequestHandler<,>) || definition == typeof(IStreamRequestHandler<,>))
            {
                // IRequestHandler<TRequest> extends IRequestHandler<TRequest, Unit>, so a
                // void-returning handler lands here too and registers against the two-argument
                // form. One shape for the dispatcher to look up, always.
                if (exclusiveHandlers.TryGetValue(implemented, out var existing))
                {
                    if (existing == implementation)
                    {
                        continue;
                    }

                    throw new DuplicateHandlerException(
                        implemented.GetGenericArguments()[0], existing, implementation);
                }

                exclusiveHandlers.Add(implemented, implementation);
                services.Add(new ServiceDescriptor(implemented, implementation, lifetime));
            }
            else if (definition == typeof(INotificationHandler<>))
            {
                // Many handlers per notification is the point, but the same implementation
                // registered twice would fire twice, so dedupe on the pair.
                services.TryAddEnumerable(new ServiceDescriptor(implemented, implementation, lifetime));
            }
        }
    }

    private static void GuardAgainstOpenGenericHandler(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] Type type)
    {
        foreach (var implemented in type.GetInterfaces())
        {
            if (!implemented.IsGenericType)
            {
                continue;
            }

            var definition = implemented.GetGenericTypeDefinition();

            if (definition == typeof(IRequestHandler<,>) ||
                definition == typeof(IStreamRequestHandler<,>) ||
                definition == typeof(INotificationHandler<>))
            {
                throw new NotSupportedException(
                    $"'{type}' is an open generic handler, which Mordecai does not support. " +
                    "Close the type arguments and register a concrete handler, or use an " +
                    "open-generic pipeline behavior instead.");
            }
        }
    }

    [RequiresUnreferencedCode("Enumerating an assembly's types requires reflection.")]
    private static Type[] TypesOf(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            var failed = 0;

            foreach (var type in exception.Types)
            {
                if (type is null)
                {
                    failed++;
                }
            }

            var reasons = new HashSet<string>(StringComparer.Ordinal);

            foreach (var loaderException in exception.LoaderExceptions)
            {
                if (loaderException is not null)
                {
                    reasons.Add(loaderException.Message);
                }
            }

            throw new InvalidOperationException(
                $"Could not load {failed} of {exception.Types.Length} types from assembly " +
                $"'{assembly.FullName}' while scanning for handlers:{Environment.NewLine}" +
                string.Join(Environment.NewLine, reasons),
                exception);
        }
    }
}
