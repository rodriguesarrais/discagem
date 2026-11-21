namespace Discagem.Tests;

/// <summary>
/// Tests that check audio heard in real time over UDP: they run on their own, after the parallel
/// tests, because a busy machine delays packets past the jitter buffer and the tones measure low.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealTimeCollection
{
    public const string Name = "Real-time media";
}
