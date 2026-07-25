using Microsoft.Extensions.DependencyInjection;

namespace OpenCoWork.Abstractions;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class OpenCoWorkModuleAttribute : Attribute
{
    public OpenCoWorkModuleAttribute(string id)
    {
        Id = id;
    }

    public string Id { get; }

    public string[] Dependencies { get; set; } = [];

    public int Priority { get; set; }

    public bool CanBePrimaryHost { get; set; }
}

public sealed class ModuleDescriptor
{
    public ModuleDescriptor(
        Type moduleType,
        string id,
        IEnumerable<string> dependencies,
        int priority,
        bool canBePrimaryHost)
    {
        ArgumentNullException.ThrowIfNull(moduleType);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(dependencies);

        ModuleType = moduleType;
        Id = id;
        Dependencies = Array.AsReadOnly(dependencies.ToArray());
        Priority = priority;
        CanBePrimaryHost = canBePrimaryHost;
    }

    public Type ModuleType { get; }

    public string Id { get; }

    public IReadOnlyList<string> Dependencies { get; }

    public int Priority { get; }

    public bool CanBePrimaryHost { get; }
}

public interface IOpenCoWorkModule
{
    void ConfigureServices(IServiceCollection services);

    ValueTask StartAsync(
        IServiceProvider services,
        CancellationToken cancellationToken);

    ValueTask StopAsync(
        IServiceProvider services,
        CancellationToken cancellationToken);
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ConfigSectionAttribute : Attribute
{
    public ConfigSectionAttribute(string name)
    {
        Name = name;
    }

    public string Name { get; }
}

[AttributeUsage(
    AttributeTargets.Property | AttributeTargets.Field,
    AllowMultiple = false,
    Inherited = true)]
public sealed class SecretAttribute : Attribute
{
}

public sealed class ConfigSectionDescriptor
{
    public ConfigSectionDescriptor(
        string name,
        Type sectionType,
        Func<object> createDefault,
        string jsonSchema)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(sectionType);
        ArgumentNullException.ThrowIfNull(createDefault);
        ArgumentNullException.ThrowIfNull(jsonSchema);

        Name = name;
        SectionType = sectionType;
        CreateDefault = createDefault;
        JsonSchema = jsonSchema;
    }

    public string Name { get; }

    public Type SectionType { get; }

    public Func<object> CreateDefault { get; }

    public string JsonSchema { get; }
}

public enum OpenCoWorkDiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

public sealed record OpenCoWorkDiagnostic(
    string Code,
    OpenCoWorkDiagnosticSeverity Severity,
    string Message,
    string? Path = null);

public sealed class OpenCoWorkValidationResult
{
    public OpenCoWorkValidationResult(IEnumerable<OpenCoWorkDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }

    public IReadOnlyList<OpenCoWorkDiagnostic> Diagnostics { get; }

    public bool IsValid =>
        Diagnostics.All(diagnostic => diagnostic.Severity != OpenCoWorkDiagnosticSeverity.Error);
}
