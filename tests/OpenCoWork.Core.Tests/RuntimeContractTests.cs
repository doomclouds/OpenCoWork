using System.ComponentModel.DataAnnotations;
using System.Reflection;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Configuration;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class RuntimeContractTests
{
    [Fact]
    public void Module_contract_preserves_declared_metadata()
    {
        var attribute = typeof(ExampleModule).GetCustomAttribute<OpenCoWorkModuleAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal("example-module", attribute.Id);
        Assert.Equal(["core"], attribute.Dependencies);
        Assert.Equal(7, attribute.Priority);
        Assert.True(attribute.CanBePrimaryHost);

        var dependencies = new[] { "core" };
        var descriptor = new ModuleDescriptor(
            typeof(ExampleModule),
            "example-module",
            dependencies,
            priority: 7,
            canBePrimaryHost: true);

        dependencies[0] = "changed";

        Assert.Equal("core", descriptor.Dependencies[0]);
    }

    [Fact]
    public void M1_config_contracts_have_frozen_names_and_defaults()
    {
        var runtime = new RuntimeConfig();
        var operations = new OperationsConfig();
        var invalidOperations = operations with { MinimumLogLevel = "verbose" };

        Assert.Equal(
            "runtime",
            typeof(RuntimeConfig).GetCustomAttribute<ConfigSectionAttribute>()?.Name);
        Assert.Equal(TimeSpan.FromSeconds(30), runtime.StopTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), runtime.State.BusyTimeout);
        Assert.Equal(
            "operations",
            typeof(OperationsConfig).GetCustomAttribute<ConfigSectionAttribute>()?.Name);
        Assert.Equal("information", operations.MinimumLogLevel);
        Assert.False(Validator.TryValidateObject(
            invalidOperations,
            new ValidationContext(invalidOperations),
            [],
            validateAllProperties: true));
        Assert.NotNull(
            typeof(CredentialsConfig)
                .GetProperty(nameof(CredentialsConfig.Token))
                ?.GetCustomAttribute<SecretAttribute>());

        var descriptor = new ConfigSectionDescriptor(
            "runtime",
            typeof(RuntimeConfig),
            static () => new RuntimeConfig(),
            """{"type":"object"}""");

        Assert.IsType<RuntimeConfig>(descriptor.CreateDefault());
    }

    [Fact]
    public void Validation_result_is_invalid_when_any_error_exists()
    {
        var source = new[]
        {
            new OpenCoWorkDiagnostic(
                "OCW0001",
                OpenCoWorkDiagnosticSeverity.Warning,
                "warning"),
        };
        var valid = new OpenCoWorkValidationResult(source);
        source[0] = new OpenCoWorkDiagnostic(
            "OCW0002",
            OpenCoWorkDiagnosticSeverity.Error,
            "error");

        var invalid = new OpenCoWorkValidationResult(
        [
            valid.Diagnostics[0],
            source[0],
        ]);

        Assert.True(valid.IsValid);
        Assert.False(invalid.IsValid);
    }

    [OpenCoWorkModule(
        "example-module",
        Dependencies = ["core"],
        Priority = 7,
        CanBePrimaryHost = true)]
    private sealed class ExampleModule
    {
    }

    [ConfigSection("credentials")]
    private sealed record CredentialsConfig
    {
        [Secret]
        public string Token { get; init; } = string.Empty;
    }
}
