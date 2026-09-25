namespace Mordecai;

/// <summary>
/// Continuation invoking the next behavior, or the handler itself at the end of the chain.
/// </summary>
/// <typeparam name="TResponse">The response type.</typeparam>
/// <param name="cancellationToken">Token observed for cancellation.</param>
/// <returns>The response produced by the remainder of the pipeline.</returns>
public delegate Task<TResponse> RequestHandlerDelegate<TResponse>(CancellationToken cancellationToken = default);

/// <summary>Continuation for stream pipelines.</summary>
/// <typeparam name="TResponse">The element type of the stream.</typeparam>
/// <param name="cancellationToken">Token observed for cancellation.</param>
/// <returns>The stream produced by the remainder of the pipeline.</returns>
public delegate IAsyncEnumerable<TResponse> StreamHandlerDelegate<out TResponse>(CancellationToken cancellationToken = default);

/// <summary>
/// Middleware wrapped around a request handler. Behaviors run in registration order,
/// outermost first.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
public interface IPipelineBehavior<in TRequest, TResponse>
    where TRequest : notnull
{
    /// <summary>Runs this behavior, invoking <paramref name="next"/> to continue the pipeline.</summary>
    /// <param name="request">The request instance.</param>
    /// <param name="next">The continuation. Not calling it short-circuits the pipeline.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The response.</returns>
    Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken);
}

/// <summary>Middleware wrapped around a stream request handler.</summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The element type of the stream.</typeparam>
public interface IStreamPipelineBehavior<in TRequest, TResponse>
    where TRequest : notnull
{
    /// <summary>Runs this behavior, invoking <paramref name="next"/> to continue the pipeline.</summary>
    /// <param name="request">The request instance.</param>
    /// <param name="next">The continuation.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The response stream.</returns>
    IAsyncEnumerable<TResponse> Handle(
        TRequest request,
        StreamHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken);
}

/// <summary>Runs before the handler, after all outer behaviors.</summary>
/// <typeparam name="TRequest">The request type.</typeparam>
public interface IRequestPreProcessor<in TRequest>
    where TRequest : notnull
{
    /// <summary>Inspects or mutates the request before it reaches the handler.</summary>
    /// <param name="request">The request instance.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    Task Process(TRequest request, CancellationToken cancellationToken);
}

/// <summary>Runs after the handler returns successfully.</summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
public interface IRequestPostProcessor<in TRequest, in TResponse>
    where TRequest : notnull
{
    /// <summary>Inspects the request and its response after handling.</summary>
    /// <param name="request">The request instance.</param>
    /// <param name="response">The response the handler produced.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    Task Process(TRequest request, TResponse response, CancellationToken cancellationToken);
}
