using System.ComponentModel.DataAnnotations;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Core.Configuration;

[ConfigSection("session")]
public sealed record SessionConfig
{
    [Range(1, 65_536)]
    public int EventBufferCapacity { get; init; } = 256;

    public TimeSpan StreamFlushInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    [Range(1, 1_048_576)]
    public int StreamFlushBytes { get; init; } = 8192;
}
