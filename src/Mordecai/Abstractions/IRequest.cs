namespace Mordecai;

/// <summary>
/// Non-generic marker shared by every request. Exists so the dispatcher can
/// recognise a request without knowing its response type.
/// </summary>
public interface IBaseRequest;

/// <summary>A request handled by exactly one handler, returning <typeparamref name="TResponse"/>.</summary>
/// <typeparam name="TResponse">The response type.</typeparam>
public interface IRequest<out TResponse> : IBaseRequest;

/// <summary>A request handled by exactly one handler, returning no value.</summary>
public interface IRequest : IRequest<Unit>;

/// <summary>A request producing an asynchronous stream of <typeparamref name="TResponse"/>.</summary>
/// <typeparam name="TResponse">The element type of the stream.</typeparam>
public interface IStreamRequest<out TResponse>;

/// <summary>An event delivered to zero or more handlers.</summary>
public interface INotification;
