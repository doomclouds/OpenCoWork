using System.ComponentModel.DataAnnotations;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Core.Configuration;

[ConfigSection("runtime")]
public sealed record RuntimeConfig
{
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(30);

    [Required]
    public RuntimeStateConfig State { get; init; } = new();
}

public sealed record RuntimeStateConfig
{
    public TimeSpan BusyTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

[ConfigSection("operations")]
public sealed record OperationsConfig
{
    [Required]
    [RegularExpression("^(trace|debug|information|warning|error|critical|none)$")]
    public string MinimumLogLevel { get; init; } = "information";
}
