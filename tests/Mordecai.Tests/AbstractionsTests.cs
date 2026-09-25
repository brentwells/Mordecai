namespace Mordecai.Tests;

public sealed class UnitTests
{
    [Fact]
    public void AllUnitValuesAreEqual()
    {
        Assert.Equal(Unit.Value, default);
        Assert.True(Unit.Value == default);
        Assert.Equal(0, Unit.Value.CompareTo(default));
    }

    [Fact]
    public void UnitIsZeroSized()
    {
        // A void-returning request must not pay an allocation for its response.
        Assert.Empty(typeof(Unit).GetFields(
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic));
    }

    [Fact]
    public async Task CompletedUnitTaskIsCached()
    {
        Assert.Same(Unit.Task, Unit.Task);
        Assert.Equal(Unit.Value, await Unit.Task);
    }

    [Fact]
    public async Task UnitTaskCompletesUnderTestCancellationToken()
    {
        // Also pins the xunit v3 token idiom the usage guide recommends over
        // CancellationToken.None -- if TestContext moves, this fails loudly.
        var value = await Unit.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Unit.Value, value);
    }
}

public sealed record GetOrderStatus(int OrderId) : IRequest<string>;

public sealed record Fire : IRequest;

public sealed class AbstractionsTests
{
    [Fact]
    public void RequestWithResponseImplementsBaseMarker()
    {
        Assert.IsAssignableFrom<IBaseRequest>(new GetOrderStatus(42));
        Assert.IsAssignableFrom<IRequest<string>>(new GetOrderStatus(42));
    }

    [Fact]
    public void VoidRequestRespondsWithUnit()
    {
        Assert.IsAssignableFrom<IRequest<Unit>>(new Fire());
    }
}
