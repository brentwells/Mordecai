using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Mordecai.Internal;

/// <summary>
/// Per-container cache of the type-closed wrappers the dispatch path needs. Registered as a
/// singleton by <c>AddMordecai</c>; a hand-constructed <see cref="Mediator"/> gets its own.
/// </summary>
/// <remarks>
/// Tying the cache to the container, rather than making it static, matters: wrappers may memoise
/// facts about the registrations (whether any behavior applies to a request type, for one) and
/// those facts are container-specific.
/// </remarks>
internal sealed class HandlerWrapperCache
{
    private readonly ConcurrentDictionary<Type, object> _requestWrappers = new();
    private readonly ConcurrentDictionary<Type, ObjectRequestHandlerWrapper> _objectRequestWrappers = new();
    private readonly ConcurrentDictionary<Type, NotificationHandlerWrapper> _notificationWrappers = new();
    private readonly ConcurrentDictionary<Type, object> _streamWrappers = new();
    private readonly ConcurrentDictionary<Type, ObjectStreamHandlerWrapper> _objectStreamWrappers = new();

    /// <summary>Gets the wrapper for <paramref name="requestType"/>, building it on first use.</summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = WrapperConstructionJustification)]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050:RequiresDynamicCode",
        Justification = WrapperConstructionJustification)]
    public RequestHandlerWrapper<TResponse> GetRequestWrapper<TResponse>(Type requestType)
    {
        if (_requestWrappers.TryGetValue(requestType, out var cached))
        {
            return (RequestHandlerWrapper<TResponse>)cached;
        }

        var created = Create(typeof(RequestHandlerWrapperImpl<,>), requestType, typeof(TResponse));

        return (RequestHandlerWrapper<TResponse>)_requestWrappers.GetOrAdd(requestType, created);
    }

    /// <summary>Gets the weakly typed request wrapper for <paramref name="requestType"/>.</summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = WrapperConstructionJustification)]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050:RequiresDynamicCode",
        Justification = WrapperConstructionJustification)]
    public ObjectRequestHandlerWrapper GetObjectRequestWrapper(Type requestType)
    {
        if (_objectRequestWrappers.TryGetValue(requestType, out var cached))
        {
            return cached;
        }

        var responseType = ResponseTypeOf(requestType);
        var created = (ObjectRequestHandlerWrapper)Create(
            typeof(ObjectRequestHandlerWrapperImpl<,>), requestType, responseType);

        return _objectRequestWrappers.GetOrAdd(requestType, created);
    }

    /// <summary>Gets the stream wrapper for <paramref name="requestType"/>.</summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = WrapperConstructionJustification)]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050:RequiresDynamicCode",
        Justification = WrapperConstructionJustification)]
    public StreamHandlerWrapper<TResponse> GetStreamWrapper<TResponse>(Type requestType)
    {
        if (_streamWrappers.TryGetValue(requestType, out var cached))
        {
            return (StreamHandlerWrapper<TResponse>)cached;
        }

        var created = Create(typeof(StreamHandlerWrapperImpl<,>), requestType, typeof(TResponse));

        return (StreamHandlerWrapper<TResponse>)_streamWrappers.GetOrAdd(requestType, created);
    }

    /// <summary>Gets the weakly typed stream wrapper for <paramref name="requestType"/>.</summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = WrapperConstructionJustification)]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050:RequiresDynamicCode",
        Justification = WrapperConstructionJustification)]
    public ObjectStreamHandlerWrapper GetObjectStreamWrapper(Type requestType)
    {
        if (_objectStreamWrappers.TryGetValue(requestType, out var cached))
        {
            return cached;
        }

        var responseType = StreamResponseTypeOf(requestType);
        var created = (ObjectStreamHandlerWrapper)Create(
            typeof(ObjectStreamHandlerWrapperImpl<,>), requestType, responseType);

        return _objectStreamWrappers.GetOrAdd(requestType, created);
    }

    /// <summary>Gets the wrapper for <paramref name="notificationType"/>.</summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = WrapperConstructionJustification)]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050:RequiresDynamicCode",
        Justification = WrapperConstructionJustification)]
    public NotificationHandlerWrapper GetNotificationWrapper(Type notificationType)
    {
        if (_notificationWrappers.TryGetValue(notificationType, out var cached))
        {
            return cached;
        }

        var created = (NotificationHandlerWrapper)Create(
            typeof(NotificationHandlerWrapperImpl<>), notificationType);

        return _notificationWrappers.GetOrAdd(notificationType, created);
    }

    /// <summary>
    /// Every entry point that can put a handler in the container — both <c>AddMordecai</c>
    /// overloads and every <c>RegisterServicesFrom*</c> method — is annotated
    /// <c>[RequiresUnreferencedCode]</c> and <c>[RequiresDynamicCode]</c>, so a trimmed or
    /// Native AOT build already warns at the composition root, naming the exact method. Letting
    /// the annotation propagate from here would additionally warn at every <c>Send</c> and
    /// <c>Publish</c> call site in the application, which adds noise and no information.
    /// See docs/guides/usage.md § Native AOT and trimming.
    /// </summary>
    private const string WrapperConstructionJustification =
        "Handler discovery is already annotated; propagating to every Send/Publish call site adds " +
        "noise without information. Mordecai does not claim Native AOT support.";

    [RequiresUnreferencedCode("Dispatch constructs wrapper types over the request type.")]
    [RequiresDynamicCode("Dispatch constructs generic wrapper types at runtime.")]
    private static object Create(Type openWrapperType, params Type[] typeArguments)
        => Activator.CreateInstance(openWrapperType.MakeGenericType(typeArguments))!;

    [RequiresUnreferencedCode("Inspecting a request's interfaces requires reflection.")]
    private static Type ResponseTypeOf(Type requestType)
    {
        foreach (var candidate in requestType.GetInterfaces())
        {
            if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IRequest<>))
            {
                return candidate.GetGenericArguments()[0];
            }
        }

        throw new ArgumentException(
            $"'{requestType}' implements {nameof(IBaseRequest)} but not IRequest<TResponse>, " +
            "so its response type cannot be determined.",
            nameof(requestType));
    }

    [RequiresUnreferencedCode("Inspecting a request's interfaces requires reflection.")]
    internal static Type StreamResponseTypeOf(Type requestType)
    {
        foreach (var candidate in requestType.GetInterfaces())
        {
            if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IStreamRequest<>))
            {
                return candidate.GetGenericArguments()[0];
            }
        }

        throw new ArgumentException(
            $"'{requestType}' does not implement IStreamRequest<TResponse>, so it is not a stream request.",
            nameof(requestType));
    }
}
