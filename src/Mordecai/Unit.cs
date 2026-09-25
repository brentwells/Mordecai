namespace Mordecai;

/// <summary>
/// The response type of a request that returns nothing. A zero-size struct, so a
/// void-returning request costs no allocation.
/// </summary>
public readonly struct Unit : IEquatable<Unit>, IComparable<Unit>, IComparable
{
    /// <summary>The single <see cref="Unit"/> value.</summary>
    public static readonly Unit Value;

    /// <summary>A completed task carrying <see cref="Value"/>. Cached; never allocates.</summary>
    public static Task<Unit> Task { get; } = System.Threading.Tasks.Task.FromResult(Value);

    /// <inheritdoc />
    public bool Equals(Unit other) => true;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Unit;

    /// <inheritdoc />
    public override int GetHashCode() => 0;

    /// <inheritdoc />
    public int CompareTo(Unit other) => 0;

    int IComparable.CompareTo(object? obj) => 0;

    /// <inheritdoc />
    public override string ToString() => "()";

    /// <summary>All <see cref="Unit"/> values are equal, so this is always <see langword="true"/>.</summary>
    public static bool operator ==(Unit left, Unit right) => true;

    /// <summary>All <see cref="Unit"/> values are equal, so this is always <see langword="false"/>.</summary>
    public static bool operator !=(Unit left, Unit right) => false;

    /// <summary>Always <see langword="false"/>; <see cref="Unit"/> has a single value.</summary>
    public static bool operator <(Unit left, Unit right) => false;

    /// <summary>Always <see langword="true"/>; <see cref="Unit"/> has a single value.</summary>
    public static bool operator <=(Unit left, Unit right) => true;

    /// <summary>Always <see langword="false"/>; <see cref="Unit"/> has a single value.</summary>
    public static bool operator >(Unit left, Unit right) => false;

    /// <summary>Always <see langword="true"/>; <see cref="Unit"/> has a single value.</summary>
    public static bool operator >=(Unit left, Unit right) => true;
}
