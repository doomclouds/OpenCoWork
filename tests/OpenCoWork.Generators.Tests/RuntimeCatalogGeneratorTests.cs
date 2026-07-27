using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace OpenCoWork.Generators.Tests;

public sealed class RuntimeCatalogGeneratorTests
{
    private static readonly CSharpParseOptions ParseOptions =
        new(LanguageVersion.CSharp14);

    [Fact]
    public void Aggregate_catalog_is_sorted_and_byte_stable()
    {
        var first = RunGenerator(AggregateSource, generateCatalog: true);
        var second = RunGenerator(AggregateSource, generateCatalog: true);

        Assert.Empty(GetAllDiagnostics(first).Where(
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Equal(
            [
                "RuntimeCatalog.Config.g.cs",
                "RuntimeCatalog.Modules.g.cs",
                "RuntimeCatalog.Wire.g.cs",
            ],
            first.Sources.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(first.Sources.Keys.Order(), second.Sources.Keys.Order());

        foreach (var hintName in first.Sources.Keys)
        {
            Assert.Equal(first.Sources[hintName], second.Sources[hintName]);
            Assert.DoesNotContain('\r', first.Sources[hintName]);
        }

        var modules = first.Sources["RuntimeCatalog.Modules.g.cs"];
        var config = first.Sources["RuntimeCatalog.Config.g.cs"];
        var wire = first.Sources["RuntimeCatalog.Wire.g.cs"];

        Assert.Equal(ExpectedModulesSnapshot, modules);
        Assert.Equal(ExpectedConfigSnapshot, config);
        Assert.Equal(ExpectedWireSnapshot, wire);
        Assert.True(
            modules.IndexOf("\"core\"", StringComparison.Ordinal) <
            modules.IndexOf("\"worker\"", StringComparison.Ordinal));
        Assert.True(
            config.IndexOf("\"operations\"", StringComparison.Ordinal) <
            config.IndexOf("\"runtime\"", StringComparison.Ordinal));
        Assert.Contains("\\\"x-opencowork-secret\\\":true", config, StringComparison.Ordinal);
        Assert.True(
            wire.IndexOf("\"thread/start\"", StringComparison.Ordinal) <
            wire.IndexOf("\"thread/stop\"", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(InvalidDeclarations))]
    public void Invalid_declarations_report_stable_diagnostics(
        string declarations,
        string expectedDiagnosticId)
    {
        var first = RunGenerator(WithContracts(declarations), generateCatalog: true);
        var second = RunGenerator(WithContracts(declarations), generateCatalog: true);
        var firstDiagnostics = GetGeneratorDiagnostics(first);
        var secondDiagnostics = GetGeneratorDiagnostics(second);

        Assert.Contains(firstDiagnostics, diagnostic => diagnostic.Id == expectedDiagnosticId);
        Assert.Equal(
            firstDiagnostics.Select(ToDiagnosticSnapshot),
            secondDiagnostics.Select(ToDiagnosticSnapshot));
    }

    [Fact]
    public void Local_compilation_validates_without_generating_catalog()
    {
        var run = RunGenerator(
            WithContracts(
                """
                namespace Sample
                {
                    [OpenCoWork.Abstractions.OpenCoWorkModule("Bad ID")]
                    public sealed class InvalidModule
                    {
                    }
                }
                """),
            generateCatalog: false);

        Assert.Empty(run.Sources);
        Assert.Contains(GetGeneratorDiagnostics(run), diagnostic => diagnostic.Id == "OCWGEN001");
    }

    [Fact]
    public void Aggregate_catalog_reads_non_prefixed_reference_with_explicit_contracts()
    {
        var contracts = CompileReference("OpenCoWork.Abstractions", ContractSource);
        var extension = CompileReference(
            "Acme.Extensions",
            """
            using OpenCoWork.Abstractions;

            namespace Acme
            {
                [OpenCoWorkModule("acme-extension")]
                public sealed class ExtensionModule
                {
                }

                [ConfigSection("acme")]
                public sealed record ExtensionConfig
                {
                }
            }
            """,
            [contracts]);
        var run = RunGenerator(
            """
            namespace Host
            {
                public sealed class EntryPoint
                {
                }
            }
            """,
            generateCatalog: true,
            [contracts, extension]);

        Assert.Empty(GetAllDiagnostics(run).Where(
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Contains(
            "global::Acme.ExtensionModule",
            run.Sources["RuntimeCatalog.Modules.g.cs"],
            StringComparison.Ordinal);
        Assert.Contains(
            "global::Acme.ExtensionConfig",
            run.Sources["RuntimeCatalog.Config.g.cs"],
            StringComparison.Ordinal);
    }

    public static TheoryData<string, string> InvalidDeclarations =>
        new()
        {
            {
                """
                namespace Sample
                {
                    [OpenCoWork.Abstractions.OpenCoWorkModule("Bad ID")]
                    public sealed class InvalidModule
                    {
                    }
                }
                """,
                "OCWGEN001"
            },
            {
                """
                namespace Sample
                {
                    [OpenCoWork.Abstractions.OpenCoWorkModule("duplicate")]
                    public sealed class FirstModule
                    {
                    }

                    [OpenCoWork.Abstractions.OpenCoWorkModule("duplicate")]
                    public sealed class SecondModule
                    {
                    }
                }
                """,
                "OCWGEN002"
            },
            {
                """
                namespace Sample
                {
                    [OpenCoWork.Abstractions.OpenCoWorkModule(
                        "worker",
                        Dependencies = new[] { "missing" })]
                    public sealed class WorkerModule
                    {
                    }
                }
                """,
                "OCWGEN003"
            },
            {
                """
                namespace Sample
                {
                    [OpenCoWork.Abstractions.OpenCoWorkModule(
                        "first",
                        Dependencies = new[] { "second" })]
                    public sealed class FirstModule
                    {
                    }

                    [OpenCoWork.Abstractions.OpenCoWorkModule(
                        "second",
                        Dependencies = new[] { "first" })]
                    public sealed class SecondModule
                    {
                    }
                }
                """,
                "OCWGEN004"
            },
            {
                """
                namespace Sample
                {
                    [OpenCoWork.Abstractions.ConfigSection("Bad.Section")]
                    public sealed class InvalidConfig
                    {
                    }
                }
                """,
                "OCWGEN005"
            },
            {
                """
                namespace Sample
                {
                    [OpenCoWork.Abstractions.ConfigSection("runtime")]
                    public sealed class FirstConfig
                    {
                    }

                    [OpenCoWork.Abstractions.ConfigSection("runtime")]
                    public sealed class SecondConfig
                    {
                    }
                }
                """,
                "OCWGEN006"
            },
            {
                """
                namespace Sample
                {
                    public static class WireMethods
                    {
                        [OpenCoWork.Abstractions.OpenCoWorkWireMethod("thread/start")]
                        public static void First()
                        {
                        }

                        [OpenCoWork.Abstractions.OpenCoWorkWireMethod("thread/start")]
                        public static void Second()
                        {
                        }
                    }
                }
                """,
                "OCWGEN007"
            },
            {
                """
                namespace Sample
                {
                    [OpenCoWork.Abstractions.ConfigSection("runtime")]
                    internal sealed class InvalidConfig
                    {
                        public InvalidConfig(int value)
                        {
                        }
                    }
                }
                """,
                "OCWGEN008"
            },
            {
                """
                namespace Sample
                {
                    [OpenCoWork.Abstractions.OpenCoWorkModule(
                        "worker",
                        Dependencies = new[] { "Bad ID" })]
                    public sealed class WorkerModule
                    {
                    }
                }
                """,
                "OCWGEN001"
            },
            {
                """
                namespace Sample
                {
                    [OpenCoWork.Abstractions.ConfigSection("runtime")]
                    public sealed class MutableConfig
                    {
                        public string Value { get; set; } = string.Empty;
                    }
                }
                """,
                "OCWGEN008"
            },
            {
                """
                namespace Sample
                {
                    [OpenCoWork.Abstractions.OpenCoWorkModule(null)]
                    public sealed class InvalidModule
                    {
                    }
                }
                """,
                "OCWGEN008"
            },
        };

    private static GeneratorRun RunGenerator(
        string source,
        bool generateCatalog,
        IEnumerable<MetadataReference>? additionalReferences = null)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, ParseOptions);
        var references = GetPlatformReferences();

        if (additionalReferences is not null)
        {
            references = references.AddRange(additionalReferences);
        }

        var compilation = CSharpCompilation.Create(
            "GeneratorTests",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var options = new TestAnalyzerConfigOptionsProvider(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["build_property.OpenCoWorkGenerateCatalog"] = generateCatalog.ToString(),
            });
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new OpenCoWorkGenerator().AsSourceGenerator()],
            parseOptions: ParseOptions,
            optionsProvider: options);

        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var outputCompilation,
            out var driverDiagnostics);

        var runResult = driver.GetRunResult();
        var result = Assert.Single(runResult.Results);
        var sources = result.GeneratedSources.ToImmutableDictionary(
            generated => generated.HintName,
            generated => generated.SourceText.ToString(),
            StringComparer.Ordinal);

        return new GeneratorRun(
            sources,
            result.Diagnostics,
            driverDiagnostics.AddRange(outputCompilation.GetDiagnostics()));
    }

    private static PortableExecutableReference CompileReference(
        string assemblyName,
        string source,
        IEnumerable<MetadataReference>? additionalReferences = null)
    {
        var references = GetPlatformReferences();

        if (additionalReferences is not null)
        {
            references = references.AddRange(additionalReferences);
        }

        var compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source, ParseOptions)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emitResult = compilation.Emit(stream);

        Assert.True(
            emitResult.Success,
            string.Join(
                Environment.NewLine,
                emitResult.Diagnostics.Select(diagnostic => diagnostic.ToString())));

        return MetadataReference.CreateFromImage(stream.ToArray());
    }

    private static ImmutableArray<Diagnostic> GetGeneratorDiagnostics(GeneratorRun run)
    {
        return
        [
            .. run.GeneratorDiagnostics
                .Where(diagnostic => diagnostic.Id.StartsWith("OCWGEN", StringComparison.Ordinal)),
        ];
    }

    private static ImmutableArray<Diagnostic> GetAllDiagnostics(GeneratorRun run) =>
        run.GeneratorDiagnostics.AddRange(run.CompilationDiagnostics);

    private static string ToDiagnosticSnapshot(Diagnostic diagnostic) =>
        $"{diagnostic.Id}:{diagnostic.GetMessage()}";

    private static string WithContracts(string declarations) =>
        ContractSource + Environment.NewLine + declarations;

    private static ImmutableArray<MetadataReference> GetPlatformReferences()
    {
        var assemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException("Trusted platform assemblies are unavailable.");

        return
        [
            .. assemblies
                .Split(Path.PathSeparator)
                .Select(path => MetadataReference.CreateFromFile(path)),
        ];
    }

    private sealed record GeneratorRun(
        ImmutableDictionary<string, string> Sources,
        ImmutableArray<Diagnostic> GeneratorDiagnostics,
        ImmutableArray<Diagnostic> CompilationDiagnostics);

    private sealed class TestAnalyzerConfigOptionsProvider : AnalyzerConfigOptionsProvider
    {
        private static readonly AnalyzerConfigOptions Empty =
            new DictionaryAnalyzerConfigOptions(new Dictionary<string, string>());
        private readonly AnalyzerConfigOptions _globalOptions;

        public TestAnalyzerConfigOptionsProvider(IReadOnlyDictionary<string, string> values)
        {
            _globalOptions = new DictionaryAnalyzerConfigOptions(values);
        }

        public override AnalyzerConfigOptions GlobalOptions => _globalOptions;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Empty;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Empty;
    }

    private sealed class DictionaryAnalyzerConfigOptions : AnalyzerConfigOptions
    {
        private readonly IReadOnlyDictionary<string, string> _values;

        public DictionaryAnalyzerConfigOptions(IReadOnlyDictionary<string, string> values)
        {
            _values = values;
        }

        public override bool TryGetValue(string key, out string value) =>
            _values.TryGetValue(key, out value!);
    }

    private static readonly string AggregateSource =
        ContractSource + Environment.NewLine + AggregateDeclarations;

    private const string ContractSource = """
        using System;
        using System.ComponentModel.DataAnnotations;
        using OpenCoWork.Abstractions;

        namespace OpenCoWork.Abstractions
        {
            [AttributeUsage(AttributeTargets.Class)]
            public sealed class OpenCoWorkModuleAttribute : Attribute
            {
                public OpenCoWorkModuleAttribute(string id) => Id = id;
                public string Id { get; }
                public string[] Dependencies { get; set; } = Array.Empty<string>();
                public int Priority { get; set; }
                public bool CanBePrimaryHost { get; set; }
            }

            [AttributeUsage(AttributeTargets.Class)]
            public sealed class ConfigSectionAttribute : Attribute
            {
                public ConfigSectionAttribute(string name) => Name = name;
                public string Name { get; }
            }

            [AttributeUsage(AttributeTargets.Property)]
            public sealed class SecretAttribute : Attribute
            {
            }

            [AttributeUsage(AttributeTargets.Method)]
            public sealed class OpenCoWorkWireMethodAttribute : Attribute
            {
                public OpenCoWorkWireMethodAttribute(string method) => Method = method;
                public string Method { get; }
            }

            public sealed class ModuleDescriptor
            {
                public ModuleDescriptor(
                    Type moduleType,
                    string id,
                    string[] dependencies,
                    int priority,
                    bool canBePrimaryHost)
                {
                }
            }

            public sealed class ConfigSectionDescriptor
            {
                public ConfigSectionDescriptor(
                    string name,
                    Type sectionType,
                    Func<object> createDefault,
                    string jsonSchema)
                {
                }
            }
        }
        """;

    private const string AggregateDeclarations = """
        namespace Sample
        {
            [OpenCoWorkModule("worker", Dependencies = new[] { "core" }, Priority = 20)]
            public sealed class WorkerModule
            {
            }

            [OpenCoWorkModule("core", Priority = 10, CanBePrimaryHost = true)]
            public sealed class CoreModule
            {
            }

            [ConfigSection("runtime")]
            public sealed record RuntimeConfig
            {
                [Range(1, 120)]
                public int MaxWorkers { get; init; } = 8;

                public global::System.Collections.Generic.Dictionary<string, ModelConfig> Models { get; init; } = new();

                public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(30);
            }

            public sealed record ModelConfig
            {
                [Required]
                public string Name { get; init; } = string.Empty;
            }

            [ConfigSection("operations")]
            public sealed record OperationsConfig
            {
                [Required]
                [Secret]
                [RegularExpression("^(information|warning|error)$")]
                public string MinimumLogLevel { get; init; } = "information";
            }

            public static class WireMethods
            {
                [OpenCoWorkWireMethod("thread/stop")]
                public static void Stop()
                {
                }

                [OpenCoWorkWireMethod("thread/start")]
                public static void Start()
                {
                }
            }
        }
        """;

    private const string ExpectedModulesSnapshot = """
        // <auto-generated/>
        #nullable enable

        namespace OpenCoWork.Generated;

        internal static partial class RuntimeCatalog
        {
            internal static global::System.Collections.Generic.IReadOnlyList<global::OpenCoWork.Abstractions.ModuleDescriptor> Modules { get; } =
                new global::OpenCoWork.Abstractions.ModuleDescriptor[]
                {
                    new global::OpenCoWork.Abstractions.ModuleDescriptor(
                        typeof(global::Sample.CoreModule),
                        "core",
                        new string[] {},
                        10,
                        true),
                    new global::OpenCoWork.Abstractions.ModuleDescriptor(
                        typeof(global::Sample.WorkerModule),
                        "worker",
                        new string[] { "core" },
                        20,
                        false),
                };
        }
        """ + "\n";

    private const string ExpectedConfigSnapshot = """
        // <auto-generated/>
        #nullable enable

        namespace OpenCoWork.Generated;

        internal static partial class RuntimeCatalog
        {
            internal static global::System.Collections.Generic.IReadOnlyList<global::OpenCoWork.Abstractions.ConfigSectionDescriptor> ConfigSections { get; } =
                new global::OpenCoWork.Abstractions.ConfigSectionDescriptor[]
                {
                    new global::OpenCoWork.Abstractions.ConfigSectionDescriptor(
                        "operations",
                        typeof(global::Sample.OperationsConfig),
                        static () => new global::Sample.OperationsConfig(),
                        "{\"type\":\"object\",\"properties\":{\"minimumLogLevel\":{\"type\":\"string\",\"pattern\":\"^(information|warning|error)$\",\"x-opencowork-secret\":true}},\"required\":[\"minimumLogLevel\"],\"additionalProperties\":false}"),
                    new global::OpenCoWork.Abstractions.ConfigSectionDescriptor(
                        "runtime",
                        typeof(global::Sample.RuntimeConfig),
                        static () => new global::Sample.RuntimeConfig(),
                        "{\"type\":\"object\",\"properties\":{\"maxWorkers\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":120},\"models\":{\"type\":\"object\",\"additionalProperties\":{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"}},\"required\":[\"name\"],\"additionalProperties\":false}},\"stopTimeout\":{\"type\":\"string\",\"format\":\"duration\"}},\"additionalProperties\":false}"),
                };
        }
        """ + "\n";

    private const string ExpectedWireSnapshot = """
        // <auto-generated/>
        #nullable enable

        namespace OpenCoWork.Generated;

        internal static partial class RuntimeCatalog
        {
            internal sealed record WireMethodDescriptor(string Method, string ContainingType, string MemberName);

            internal static global::System.Collections.Generic.IReadOnlyList<WireMethodDescriptor> WireMethods { get; } =
                new WireMethodDescriptor[]
                {
                    new WireMethodDescriptor("thread/start", "Sample.WireMethods", "Start"),
                    new WireMethodDescriptor("thread/stop", "Sample.WireMethods", "Stop"),
                };
        }
        """ + "\n";
}
