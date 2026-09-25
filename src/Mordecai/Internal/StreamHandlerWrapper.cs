using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace Mordecai.Internal;

/// <summary>
/// The streaming counterpart to <see cref="RequestHandlerWrapper{TResponse}"/>: closes over the
/// concrete stream request type so <c>CreateStream&lt;TResponse&gt;</c> can reach its handler.
/// </summary>
/// <typeparam name="TResponse">The element type of the stream.</typeparam>
internal abstract class StreamHandlerWrapper<TResponse>
{
    public abstract IAsyncEnumerable<TResponse> Handle(
        IStreamRequest<TResponse> request,
        IServiceProvider services,
        CancellationToken cancellationToken);
}

/// <summary>The <typeparamref name="TRequest"/>-closed implementation.</summary>
/// <typeparam name="TRequest">The concrete stream request type.</typeparam>
/// <typeparam name="TResponse">The element type of the stream.</typeparam>
internal sealed class StreamHandlerWrapperImpl<TRequest, TResponse> : StreamHandlerWrapper<TResponse>
    where TRequest : IStreamRequest<TResponse>
{
    private volatile int _behaviorCount = -1;

    public override IAsyncEnumerable<TResponse> Handle(
        IStreamRequest<TResponse> request,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var handler = services.GetService<IStreamRequestHandler<TRequest, TResponse>>();

        if (handler is null)
        {
            return MissingHandler(services);
        }

        var typed = (TRequest)request;

        // Returning the handler's own enumerable rather than wrapping it in another iterator keeps
        // [EnumeratorCancellation] working end to end: a token the caller supplies through
        // WithCancellation still reaches the handler's state machine.
        if (_behaviorCount == 0)
        {
            return handler.Handle(typed, cancellationToken);
        }

        var resolved = services.GetServices<IStreamPipelineBehavior<TRequest, TResponse>>();
        var behaviors = resolved as IStreamPipelineBehavior<TRequest, TResponse>[] ?? [.. resolved];

        _behaviorCount = behaviors.Length;

        return behaviors.Length == 0
            ? handler.Handle(typed, cancellationToken)
            : Compose(behaviors, handler, typed)(cancellationToken);
    }

    private static StreamHandlerDelegate<TResponse> Compose(
        IStreamPipelineBehavior<TRequest, TResponse>[] behaviors,
        IStreamRequestHandler<TRequest, TResponse> handler,
        TRequest request)
    {
        StreamHandlerDelegate<TResponse> next = ct => handler.Handle(request, ct);

        // Same reverse fold as the request pipeline: first registered, outermost.
        for (var i = behaviors.Length - 1; i >= 0; i--)
        {
            var behavior = behaviors[i];
            var continuation = next;

            next = ct => behavior.Handle(request, continuation, ct);
        }

        return next;
    }

    private static IAsyncEnumerable<TResponse> MissingHandler(IServiceProvider services)
    {
        var options = services.GetService<MordecaiRuntimeOptions>();

        return options is not null && !options.ThrowOnMissingHandler
            ? AsyncEnumerable.Empty<TResponse>()
            : throw new HandlerNotFoundException(typeof(TRequest));
    }
}

/// <summary>The weakly typed <c>CreateStream(object)</c> counterpart.</summary>
internal abstract class ObjectStreamHandlerWrapper
{
    public abstract IAsyncEnumerable<object?> Handle(
        object request,
        IServiceProvider services,
        CancellationToken cancellationToken);
}

/// <summary>The fully closed implementation behind <c>CreateStream(object)</c>.</summary>
/// <typeparam name="TRequest">The concrete stream request type.</typeparam>
/// <typeparam name="TResponse">The element type of the stream.</typeparam>
internal sealed class ObjectStreamHandlerWrapperImpl<TRequest, TResponse> : ObjectStreamHandlerWrapper
    where TRequest : IStreamRequest<TResponse>
{
    private readonly StreamHandlerWrapperImpl<TRequest, TResponse> _inner = new();

    public override async IAsyncEnumerable<object?> Handle(
        object request,
        IServiceProvider services,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var source = _inner.Handle((IStreamRequest<TResponse>)request, services, cancellationToken);

        await foreach (var item in source.ConfigureAwait(false).WithCancellation(cancellationToken))
        {
            yield return item;
        }
    }
}
