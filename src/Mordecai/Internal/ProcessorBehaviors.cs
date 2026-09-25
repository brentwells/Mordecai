namespace Mordecai.Internal;

/// <summary>
/// Runs every registered <see cref="IRequestPreProcessor{TRequest}"/> and then continues.
/// Processors are behaviors underneath — there is no second mechanism — which is why they sit
/// inside the user's own pipeline rather than around it.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
internal sealed class RequestPreProcessorBehavior<TRequest, TResponse>(
    IEnumerable<IRequestPreProcessor<TRequest>> preProcessors)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        foreach (var preProcessor in preProcessors)
        {
            await preProcessor.Process(request, cancellationToken).ConfigureAwait(false);
        }

        return await next(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Runs every registered <see cref="IRequestPostProcessor{TRequest, TResponse}"/> once the handler
/// has returned. Registered inside the pre-processor behavior, so nothing sits between the handler
/// returning and the post-processors seeing its response.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
internal sealed class RequestPostProcessorBehavior<TRequest, TResponse>(
    IEnumerable<IRequestPostProcessor<TRequest, TResponse>> postProcessors)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        // No try/finally: a handler that threw has no response to post-process, and the guide is
        // explicit that cleanup which must always happen belongs in a behavior instead.
        var response = await next(cancellationToken).ConfigureAwait(false);

        foreach (var postProcessor in postProcessors)
        {
            await postProcessor.Process(request, response, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }
}
