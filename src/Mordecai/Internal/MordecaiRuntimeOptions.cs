namespace Mordecai.Internal;

/// <summary>
/// Runtime-relevant slice of <c>MordecaiConfiguration</c>, registered as a singleton so the
/// dispatch path can read it without holding a reference to the whole configuration object.
/// Absent when a <see cref="Mediator"/> is constructed by hand, in which case defaults apply.
/// </summary>
internal sealed class MordecaiRuntimeOptions
{
    /// <summary>Whether <c>Send</c> throws when no handler is registered for the request.</summary>
    public bool ThrowOnMissingHandler { get; init; } = true;
}
