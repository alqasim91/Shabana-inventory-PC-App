namespace Shabana.Tests.Data;

/// <summary>A clock the test moves by hand.</summary>
internal sealed class FakeTime : TimeProvider
{
    private DateTimeOffset _now;

    public FakeTime(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
