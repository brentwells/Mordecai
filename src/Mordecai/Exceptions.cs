namespace Mordecai;

/// <summary>
/// Thrown when a request is dispatched and no handler for it is registered with the container.
/// </summary>
public sealed class HandlerNotFoundException : InvalidOperationException
{
    /// <summary>Creates an exception with the default message.</summary>
    public HandlerNotFoundException()
        : base("No handler was registered for the request.")
    {
    }

    /// <summary>Creates an exception with the supplied message.</summary>
    /// <param name="message">The message.</param>
    public HandlerNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with the supplied message and cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The underlying cause.</param>
    public HandlerNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates an exception naming the request type that could not be handled.</summary>
    /// <param name="requestType">The request type with no registered handler.</param>
    public HandlerNotFoundException(Type requestType)
        : base(BuildMessage(requestType))
        => RequestType = requestType;

    /// <summary>The request type that could not be handled, when known.</summary>
    public Type? RequestType { get; }

    private static string BuildMessage(Type requestType)
        => $"No handler was registered for request type '{requestType}'. " +
           "Register the handler explicitly, or pass the assembly that contains it to " +
           "AddMordecai(cfg => cfg.RegisterServicesFromAssemblyContaining<T>()).";
}

/// <summary>
/// Thrown during registration when two handlers claim the same request. A request has exactly
/// one handler; notifications, which may have many, are exempt.
/// </summary>
public sealed class DuplicateHandlerException : InvalidOperationException
{
    /// <summary>Creates an exception with the default message.</summary>
    public DuplicateHandlerException()
        : base("More than one handler was registered for the same request.")
    {
    }

    /// <summary>Creates an exception with the supplied message.</summary>
    /// <param name="message">The message.</param>
    public DuplicateHandlerException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with the supplied message and cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The underlying cause.</param>
    public DuplicateHandlerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates an exception naming the request and both competing handlers.</summary>
    /// <param name="requestType">The request both handlers claim.</param>
    /// <param name="first">The handler discovered first.</param>
    /// <param name="second">The handler discovered second.</param>
    public DuplicateHandlerException(Type requestType, Type first, Type second)
        : base(BuildMessage(requestType, first, second))
    {
        RequestType = requestType;
        FirstHandlerType = first;
        SecondHandlerType = second;
    }

    /// <summary>The request type both handlers claim, when known.</summary>
    public Type? RequestType { get; }

    /// <summary>The handler discovered first, when known.</summary>
    public Type? FirstHandlerType { get; }

    /// <summary>The handler discovered second, when known.</summary>
    public Type? SecondHandlerType { get; }

    private static string BuildMessage(Type requestType, Type first, Type second)
        => $"Two handlers were registered for request type '{requestType}': " +
           $"'{first}' and '{second}'. A request is handled by exactly one handler. " +
           "Remove one of them, or stop scanning the assembly you did not mean to include.";
}
