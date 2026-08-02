using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCoWork.Abstractions;
using OpenCoWork.App;
using OpenCoWork.Automations;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Diagnostics;
using OpenCoWork.Protocol;
using OpenCoWork.Teams;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class RuntimeContractSnapshotTests
{
    private static readonly JsonSerializerOptions SnapshotOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [Fact]
    public void Runtime_10_public_contract_matches_the_golden_snapshot()
    {
        var actual = CreateSnapshot();
        var path = SnapshotPath();

        Assert.True(File.Exists(path), $"Missing contract snapshot.\n{actual}");
        Assert.Equal(File.ReadAllText(path), actual);
    }

    private static string CreateSnapshot()
    {
        var catalog = typeof(OpenCoWorkCli).Assembly.GetType(
            "OpenCoWork.Generated.RuntimeCatalog",
            throwOnError: true)!;
        var flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var methods = typeof(OpenCoWorkJsonRpcConnection).Assembly.GetTypes()
            .Where(type => type == typeof(OpenCoWorkJsonRpcConnection) ||
                           type.Name.EndsWith("WireCatalog", StringComparison.Ordinal))
            .SelectMany(type => type.GetMethods(
                BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.Public | BindingFlags.NonPublic))
            .Select(method => method.GetCustomAttribute<OpenCoWorkWireMethodAttribute>())
            .Where(attribute => attribute is not null)
            .Cast<OpenCoWorkWireMethodAttribute>()
            .Select(attribute => new WireMethodDescriptor(
                attribute.Method,
                attribute.Direction,
                attribute.Owner,
                attribute.Since,
                attribute.Request,
                attribute.Response,
                attribute.Authority,
                attribute.Mutates,
                attribute.Idempotency))
            .OrderBy(method => method.Method, StringComparer.Ordinal)
            .ToArray();
        var sections = ((IReadOnlyList<ConfigSectionDescriptor>)catalog
                .GetProperty("ConfigSections", flags)!
                .GetValue(null)!)
            .OrderBy(section => section.Name, StringComparer.Ordinal)
            .ToArray();
        var metadata = ProductMetadata.FromAssembly(typeof(OpenCoWorkCli).Assembly);
        var snapshot = new
        {
            SchemaVersion = 1,
            Product = new
            {
                metadata.ProductVersion,
                InformationalVersion = metadata.InformationalVersion.Split('+')[0],
                AssemblyVersion = typeof(OpenCoWorkCli).Assembly.GetName().Version!.ToString(),
            },
            Wire = new
            {
                LatestVersion = OpenCoWorkWire.LatestVersion,
                Methods = methods.Select(method => method.Method),
                MethodDescriptorsSha256 = Sha256(string.Join('\n',
                    methods.Select(method => string.Join('|',
                        method.Method,
                        method.Direction,
                        method.Owner,
                        method.Since,
                        TypeName(method.Request),
                        TypeName(method.Response),
                        method.Authority,
                        method.Mutates ? "mutates" : "reads",
                        method.Idempotency)))),
                TypeCount = ContractTypes(methods).Count,
                TypeShapesSha256 = Sha256(string.Join('\n',
                    ContractTypes(methods).Select(TypeShape))),
            },
            Configuration = sections.Select(section => new
            {
                section.Name,
                Type = TypeName(section.SectionType),
                Default = JsonSerializer.SerializeToElement(
                    section.CreateDefault(),
                    section.SectionType,
                    ConfigLoader.SerializerOptions),
                SchemaSha256 = Sha256(section.JsonSchema),
            }),
            Provider = new
            {
                Id = ModelsConfig.ProviderId,
                Models = new[] { ModelsConfig.FlashModelId },
                AuthProfile = ModelsConfig.AuthProfileId,
                ApiKeyEnvironmentVariable = ModelsConfig.ApiKeyEnvironmentVariable,
            },
            Plugin = new
            {
                Manifest = new
                {
                    SchemaVersion = 1,
                    HostApiVersion = 1,
                    Required = new[]
                    {
                        "schemaVersion", "hostApiVersion", "id", "version",
                        "displayName", "contributions",
                    },
                    Optional = new[] { "entryPoint" },
                    Contributions = new[]
                    {
                        "skills", "providers", "authProfiles", "mcpServers",
                        "lspServers", "tools", "hooks",
                    },
                },
                Lock = new
                {
                    SchemaVersion = 1,
                    Entry = new[] { "id", "version", "sha256", "enabled" },
                },
            },
            ErrorCodes = ErrorCodes()
                .Select(value => value[(value.IndexOf('=') + 1)..])
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        };
        return JsonSerializer.Serialize(snapshot, SnapshotOptions) + Environment.NewLine;
    }

    private static IReadOnlyList<Type> ContractTypes(
        IReadOnlyList<WireMethodDescriptor> methods)
    {
        var result = new HashSet<Type>();
        var pending = new Queue<Type>(methods.SelectMany(method =>
            new[] { method.Request, method.Response }));
        while (pending.TryDequeue(out var candidate))
        {
            var type = Nullable.GetUnderlyingType(candidate) ?? candidate;
            if (type.IsArray)
            {
                pending.Enqueue(type.GetElementType()!);
                continue;
            }
            if (type.IsGenericType)
            {
                foreach (var argument in type.GetGenericArguments())
                {
                    pending.Enqueue(argument);
                }
            }
            if (type.Namespace?.StartsWith("OpenCoWork", StringComparison.Ordinal) != true ||
                !result.Add(type))
            {
                continue;
            }
            foreach (var property in type.GetProperties(
                         BindingFlags.Instance | BindingFlags.Public))
            {
                pending.Enqueue(property.PropertyType);
            }
        }
        return result.OrderBy(TypeName, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<string> ErrorCodes()
    {
        var assemblies = new[]
        {
            typeof(OpenCoWorkWire).Assembly,
            typeof(ConfigLoader).Assembly,
            typeof(OpenCoWorkJsonRpcConnection).Assembly,
            typeof(AutomationsConfig).Assembly,
            typeof(CoWorkService).Assembly,
            typeof(OpenCoWorkCli).Assembly,
        };
        return assemblies
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.Name.EndsWith("ErrorCodes", StringComparison.Ordinal))
            .SelectMany(type => type.GetFields(
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(field => field is { IsLiteral: true, FieldType: not null } &&
                                field.FieldType == typeof(string))
                .Select(field => $"{type.FullName}.{field.Name}={field.GetRawConstantValue()}"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string TypeName(Type type)
    {
        if (type.IsArray)
        {
            return TypeName(type.GetElementType()!) + "[]";
        }
        var nullable = Nullable.GetUnderlyingType(type);
        if (nullable is not null)
        {
            return TypeName(nullable) + "?";
        }
        if (!type.IsGenericType)
        {
            return type.FullName ?? type.Name;
        }
        var name = type.GetGenericTypeDefinition().FullName!;
        name = name[..name.IndexOf('`')];
        return $"{name}<{string.Join(',', type.GetGenericArguments().Select(TypeName))}>";
    }

    private static string TypeShape(Type type) => type.IsEnum
        ? $"{TypeName(type)}={string.Join(',', Enum.GetNames(type))}"
        : $"{TypeName(type)}={string.Join(',', type
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property =>
                $"{JsonNamingPolicy.CamelCase.ConvertName(property.Name)}:{TypeName(property.PropertyType)}"))}";

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static string SnapshotPath(
        [CallerFilePath] string sourcePath = "") =>
        Path.Combine(
            Path.GetDirectoryName(sourcePath)!,
            "Snapshots",
            "runtime-1.0-contract.snapshot.json");
}
