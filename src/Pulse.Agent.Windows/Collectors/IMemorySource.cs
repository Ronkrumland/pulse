namespace Pulse.Agent.Windows.Collectors;

public sealed record MemoryReading(float? UsedMegabytes, float? TotalMegabytes);

public interface IMemorySource
{
    MemoryReading? ReadMemory();
}
