using Microsoft.Extensions.DependencyInjection;

namespace Mordecai.Internal;

/// <summary>
/// Closes over the concrete request type so <c>Send&lt;TResponse&gt;</c> — which only knows
/// <c>IRequest&lt;TResponse&gt;</c> statically — can reach a strongly typed handler. One instance
/// per request type, built once and cached.
/// </summary>
/// <typeparam name="TResponse">The response type.</typeparam>
internal abstract class RequestHandlerWrapper<TResponse>
{
    public abstract Task<TResponse> Handle(
        IRequest<TResponse> request,
        IServiceProvider services,
        CancellationToken cancellationToken);
}

/// <summary>The <typeparamref name="TRequest"/>-closed implementation.</summary>
/// <typeparam name="TRequest">The concrete request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
internal sealed class RequestHandlerWrapperImpl<TRequest, TResponse> : RequestHandlerWrapper<TResponse>
    where TRequest : IRequest<TResponse>
{
    // How many behaviors this container has for this request/response pair, or -1 until the first
    // send finds out. Registrations are fixed once the provider is built, so a zero stays zero and
    // every later send skips resolving an empty IEnumerable -- which is not free.
    private volatile int _behaviorCount = -1;

    public override Task<TResponse> Handle(
        IRequest<TResponse> request,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var handler = services.GetService<IRequestHandler<TRequest, TResponse>>();

        if (handler is null)
        {
            return MissingHandler(services);
        }

        var typed = (TRequest)request;

        if (_behaviorCount == 0)
        {
            return handler.Handle(typed, cancellationToken);
        }

        var resolved = services.GetServices<IPipelineBehavior<TRequest, TResponse>>();
        var behaviors = resolved as IPipelineBehavior<TRequest, TResponse>[] ?? [.. resolved];

        _behaviorCount = behaviors.Length;

        return behaviors.Length == 0
            ? handler.Handle(typed, cancellationToken)
            : Compose(behaviors, handler, typed)(cancellationToken);
    }

    private static RequestHandlerDelegate<TResponse> Compose(
        IPipelineBehavior<TRequest, TResponse>[] behaviors,
        IRequestHandler<TRequest, TResponse> handler,
        TRequest request)
    {
        RequestHandlerDelegate<TResponse> next = ct => handler.Handle(request, ct);

        // Fold from the inside out, so the first-registered behavior ends up outermost. Build it
        // the other way round and every ordering guarantee in the guide silently inverts.
        for (var i = behaviors.Length - 1; i >= 0; i--)
        {
            var behavior = behaviors[i];
            var continuation = next;

            next = ct => behavior.Handle(request, continuation, ct);
        }

        return next;
    }

    private static Task<TResponse> MissingHandler(IServiceProvider services)
    {
        var options = services.GetService<MordecaiRuntimeOptions>();

        return options is not null && !options.ThrowOnMissingHandler
            ? Task.FromResult<TResponse>(default!)
            : throw new HandlerNotFoundException(typeof(TRequest));
    }
}

/// <summary>
/// The same trick for the weakly typed <c>Send(object)</c> overload, where neither the request
/// type nor the response type is known statically.
/// </summary>
internal abstract class ObjectRequestHandlerWrapper
{
    public abstract Task<object?> Handle(
        object request,
        IServiceProvider services,
        CancellationToken cancellationToken);
}

/// <summary>The fully closed implementation behind <c>Send(object)</c>.</summary>
/// <typeparam name="TRequest">The concrete request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
internal sealed class ObjectRequestHandlerWrapperImpl<TRequest, TResponse> : ObjectRequestHandlerWrapper
    where TRequest : IRequest<TResponse>
{
    private readonly RequestHandlerWrapperImpl<TRequest, TResponse> _inner = new();

    public override async Task<object?> Handle(
        object request,
        IServiceProvider services,
        CancellationToken cancellationToken)
        => await _inner
            .Handle((IRequest<TResponse>)request, services, cancellationToken)
            .ConfigureAwait(false);
}
