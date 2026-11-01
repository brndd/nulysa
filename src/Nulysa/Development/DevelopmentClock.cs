namespace Nulysa.Development;

// Development-only wall clock used while composing the demo dataset.
public sealed class DevelopmentClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => Now;
}
