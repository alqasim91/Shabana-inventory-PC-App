using NodaTime;

namespace Shabana.Core.Time;

/// <summary>Source of "now" (04 section 7.4). Production uses <see cref="SystemUtcClock"/>; tests inject a fixed one.</summary>
public interface IClock
{
    Instant UtcNow { get; }
}

public sealed class SystemUtcClock : IClock
{
    public Instant UtcNow => SystemClock.Instance.GetCurrentInstant();
}
