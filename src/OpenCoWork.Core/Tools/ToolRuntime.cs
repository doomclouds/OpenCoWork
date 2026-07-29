using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Json.Schema;
using Json.Schema.Keywords;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Workspaces;

namespace OpenCoWork.Core.Tools;

internal sealed partial class ToolRuntime
{
    private const int SnapshotSchemaVersion = 2;
    private const ToolEffect KnownEffects =
        ToolEffect.WorkspaceRead |
        ToolEffect.WorkspaceWrite |
        ToolEffect.ProcessExecution |
        ToolEffect.NetworkRead |
        ToolEffect.ExternalMutation;
    private const ToolEffect PlanEffects =
        ToolEffect.WorkspaceRead |
        ToolEffect.NetworkRead;
    private const ToolInvocationAudience KnownAudiences =
        ToolInvocationAudience.Model |
        ToolInvocationAudience.Host |
        ToolInvocationAudience.App;
    private static readonly Uri Draft202012Uri =
        new("https://json-schema.org/draft/2020-12/schema", UriKind.Absolute);
    private static readonly HashSet<string> AllowedVocabularies =
    [
        Vocabulary.Draft202012_Core.Id.AbsoluteUri,
        Vocabulary.Draft202012_Applicator.Id.AbsoluteUri,
        Vocabulary.Draft202012_Unevaluated.Id.AbsoluteUri,
        Vocabulary.Draft202012_Validation.Id.AbsoluteUri,
        Vocabulary.Draft202012_MetaData.Id.AbsoluteUri,
        Vocabulary.Draft202012_FormatAnnotation.Id.AbsoluteUri,
        Vocabulary.Draft202012_Content.Id.AbsoluteUri,
    ];

    private readonly Candidate[] _candidates;
    private readonly object _bindingGate = new();
    private readonly Dictionary<RuntimeBindingId, ToolRuntimeBinding> _bindings;
    private readonly IReadOnlyDictionary<ToolDefinitionId, JsonSchema> _schemas;

    internal IReadOnlyList<ToolRegistration> Registrations =>
        Array.AsReadOnly(_candidates
            .Select(candidate => candidate.Registration)
            .ToArray());

    public ToolRuntime()
        : this(CreateCoreTools(
            fileTools: null,
            shellTool: null,
            webTool: null))
    {
    }

    internal ToolRuntime(OpenCoWorkPaths paths)
        : this(paths, models: null)
    {
    }

    internal ToolRuntime(OpenCoWorkPaths paths, ModelsConfig? models)
        : this(CreateCoreTools(
            new CoreFileTools(paths),
            new CoreShellTool(
                paths,
                models?.Providers.Values
                    .Select(provider => provider.ApiKey.Environment) ??
                []),
            new CoreWebTool()))
    {
    }

    internal ToolRuntime(IEnumerable<ToolRegistration> registrations)
        : this(registrations, [])
    {
    }

    internal ToolRuntime(
        IEnumerable<ToolRegistration> registrations,
        IEnumerable<ToolRuntimeBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(bindings);
        _candidates = registrations
            .Select(registration =>
            {
                var schema = TryCompileSchema(registration.Definition, out var compiled)
                    ? compiled
                    : null;
                return new Candidate(registration, schema);
            })
            .ToArray();

        var bindingArray = bindings.ToArray();
        if (bindingArray.Any(binding =>
                binding is null ||
                string.IsNullOrWhiteSpace(binding.Id.Value) ||
                !Enum.IsDefined(binding.Availability) ||
                binding.Generation <= 0) ||
            bindingArray
            .GroupBy(binding => binding.Id)
            .Any(group => group.Count() != 1))
        {
            throw new ToolRuntimeException(
                ToolErrorCodes.DefinitionInvalid,
                "Runtime Binding IDs must be unique.");
        }

        _bindings = bindingArray.ToDictionary(binding => binding.Id);
        _schemas = _candidates
            .Where(candidate => candidate.Schema is not null)
            .GroupBy(candidate => candidate.Registration.Definition.Id)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Schema!);
    }

    private ToolRuntime(CoreTools core)
        : this(core.Registrations, core.Bindings)
    {
    }

    public EffectiveToolSnapshot BuildSnapshot(
        AgentMode effectiveAgentMode,
        ToolsConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var authority = BuildAuthority(config);
        var diagnostics = new List<ToolSnapshotDiagnostic>();
        var eligible = new List<Candidate>();
        var conflicts = FindConflicts(_candidates);

        foreach (var candidate in Ordered(_candidates))
        {
            var registration = candidate.Registration;
            var definition = registration.Definition;
            var canonicalName = CanonicalName(definition.Name);
            string? rejection = null;
            if (conflicts.Contains(candidate))
            {
                rejection = ToolErrorCodes.NameConflict;
            }
            else if (!Enum.IsDefined(definition.Id.SourceKind) ||
                !IsCleanIdentity(definition.Id.SourceId) ||
                !IsCleanIdentity(definition.Id.SourceToolId) ||
                !NamePartPattern().IsMatch(definition.Name.Namespace) ||
                !NamePartPattern().IsMatch(definition.Name.Name) ||
                !Enum.IsDefined(definition.ReplaySafety) ||
                (definition.Effects & ~KnownEffects) != 0 ||
                candidate.Schema is null ||
                registration.BindingGeneration <= 0 ||
                !IsCleanIdentity(registration.RuntimeBindingId.Value) ||
                !Enum.IsDefined(registration.Exposure) ||
                (registration.Audience & ~KnownAudiences) != 0)
            {
                rejection = ToolErrorCodes.DefinitionInvalid;
            }
            else if ((registration.Audience & ToolInvocationAudience.Model) == 0)
            {
                rejection = ToolErrorCodes.AudienceDenied;
            }
            else if (registration.Exposure != ToolExposure.Direct)
            {
                rejection = ToolErrorCodes.ExposureDenied;
            }

            if (rejection is null)
            {
                eligible.Add(candidate);
            }
            else
            {
                diagnostics.Add(Diagnostic(rejection, definition.Id, canonicalName));
            }
        }

        var selected = new List<Candidate>();
        foreach (var candidate in eligible)
        {
            var definition = candidate.Registration.Definition;
            var canonicalName = CanonicalName(definition.Name);
            if (effectiveAgentMode == AgentMode.Plan &&
                (definition.Effects & ~PlanEffects) != 0)
            {
                diagnostics.Add(Diagnostic(
                    ToolErrorCodes.ModeDenied,
                    definition.Id,
                    canonicalName));
            }
            else if (DecisionFor(definition.Effects, authority) ==
                     ToolAuthorityDecision.Deny)
            {
                diagnostics.Add(Diagnostic(
                    ToolErrorCodes.AuthorityDenied,
                    definition.Id,
                    canonicalName));
            }
            else
            {
                selected.Add(candidate);
            }
        }

        var canonicalToProvider = ProjectProviderNames(selected, diagnostics);
        selected = selected
            .Where(candidate => canonicalToProvider.ContainsKey(
                CanonicalName(candidate.Registration.Definition.Name)))
            .ToList();
        var registrations = selected
            .Select(candidate => candidate.Registration)
            .ToArray();
        var providerToCanonical = canonicalToProvider.ToDictionary(
            pair => pair.Value,
            pair => pair.Key,
            StringComparer.Ordinal);
        var orderedDiagnostics = diagnostics
            .OrderBy(item => item.Code, StringComparer.Ordinal)
            .ThenBy(item => item.CanonicalName, StringComparer.Ordinal)
            .ThenBy(item => DefinitionKey(item.DefinitionId), StringComparer.Ordinal)
            .ToArray();
        var hashInput = WriteSnapshot(
            effectiveAgentMode,
            authority,
            registrations,
            canonicalToProvider,
            providerToCanonical,
            orderedDiagnostics,
            snapshotSha256: null);
        var snapshotSha256 = Convert.ToHexString(SHA256.HashData(hashInput))
            .ToLowerInvariant();
        var completeSnapshot = WriteSnapshot(
            effectiveAgentMode,
            authority,
            registrations,
            canonicalToProvider,
            providerToCanonical,
            orderedDiagnostics,
            snapshotSha256);
        if (completeSnapshot.Length > ToolRuntimeLimits.MaximumSnapshotBytes)
        {
            throw new ToolRuntimeException(
                ToolErrorCodes.SnapshotTooLarge,
                "The effective tool snapshot exceeds 1 MiB.");
        }

        return new EffectiveToolSnapshot(
            SnapshotSchemaVersion,
            effectiveAgentMode,
            authority,
            registrations,
            canonicalToProvider,
            providerToCanonical,
            orderedDiagnostics,
            snapshotSha256);
    }

    public IReadOnlyList<ChatCompletionToolDefinition> CreateProviderDefinitions(
        EffectiveToolSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Array.AsReadOnly(snapshot.Registrations
            .Select(registration =>
            {
                var canonical = CanonicalName(registration.Definition.Name);
                return new ChatCompletionToolDefinition(
                    snapshot.CanonicalToProviderNames[canonical],
                    registration.Definition.Description,
                    registration.Definition.InputSchema);
            })
            .ToArray());
    }

    public bool ValidateArguments(
        ToolDefinitionId definitionId,
        JsonElement arguments)
    {
        ArgumentNullException.ThrowIfNull(definitionId);
        return _schemas.TryGetValue(definitionId, out var schema) &&
               schema.Evaluate(
                   arguments,
                   new EvaluationOptions
                   {
                       OutputFormat = OutputFormat.Flag,
                       RequireFormatValidation = false,
                   }).IsValid;
    }

    public bool ValidateArguments(
        ToolDefinition definition,
        JsonElement arguments)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return TryCompileSchema(definition, out var schema) &&
               schema!.Evaluate(
                   arguments,
                   new EvaluationOptions
                   {
                       OutputFormat = OutputFormat.Flag,
                       RequireFormatValidation = false,
                   }).IsValid;
    }

    public bool TryResolveBinding(
        RuntimeBindingId bindingId,
        out ToolRuntimeBinding? binding)
    {
        ArgumentNullException.ThrowIfNull(bindingId);
        lock (_bindingGate)
        {
            return _bindings.TryGetValue(bindingId, out binding);
        }
    }

    internal void PublishBinding(ToolRuntimeBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!IsCleanIdentity(binding.Id.Value) ||
            !Enum.IsDefined(binding.Availability) ||
            binding.Generation <= 0)
        {
            throw new ToolRuntimeException(
                ToolErrorCodes.DefinitionInvalid,
                "Runtime Binding is invalid.");
        }

        lock (_bindingGate)
        {
            if (_bindings.TryGetValue(binding.Id, out var current) &&
                binding.Generation < current.Generation)
            {
                throw new ToolRuntimeException(
                    ToolErrorCodes.DefinitionInvalid,
                    "Runtime Binding generation cannot move backwards.");
            }

            if (current is not null &&
                binding.Generation == current.Generation &&
                (binding.Executor != current.Executor ||
                 binding.DefaultTimeout != current.DefaultTimeout))
            {
                throw new ToolRuntimeException(
                    ToolErrorCodes.DefinitionInvalid,
                    "Runtime Binding implementation changes require a new generation.");
            }

            _bindings[binding.Id] = binding;
        }
    }

    private static HashSet<Candidate> FindConflicts(
        IReadOnlyList<Candidate> candidates) =>
        candidates
            .GroupBy(candidate => candidate.Registration.Definition.Id)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .Concat(candidates
                .GroupBy(
                    candidate => CanonicalName(candidate.Registration.Definition.Name),
                    StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .SelectMany(group => group))
            .ToHashSet();

    private static Dictionary<string, string> ProjectProviderNames(
        IReadOnlyList<Candidate> candidates,
        List<ToolSnapshotDiagnostic> diagnostics)
    {
        var projections = candidates
            .Select(candidate =>
            {
                var canonicalName = CanonicalName(candidate.Registration.Definition.Name);
                var baseName =
                    $"{candidate.Registration.Definition.Name.Namespace}__" +
                    candidate.Registration.Definition.Name.Name;
                return new Projection(candidate, canonicalName, baseName);
            })
            .ToArray();
        var collisions = projections
            .GroupBy(item => item.BaseName, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .ToHashSet();
        var mapped = projections
            .Select(item => new
            {
                item.Candidate,
                item.CanonicalName,
                ProviderName = item.BaseName.Length <= 64 && !collisions.Contains(item)
                    ? item.BaseName
                    : HashedProviderName(item.BaseName, item.CanonicalName),
            })
            .ToArray();
        var hashCollisions = mapped
            .GroupBy(item => item.ProviderName, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .ToHashSet();
        foreach (var item in hashCollisions)
        {
            diagnostics.Add(Diagnostic(
                ToolErrorCodes.NameConflict,
                item.Candidate.Registration.Definition.Id,
                item.CanonicalName));
        }

        return mapped
            .Where(item => !hashCollisions.Contains(item))
            .OrderBy(item => item.CanonicalName, StringComparer.Ordinal)
            .ToDictionary(
                item => item.CanonicalName,
                item => item.ProviderName,
                StringComparer.Ordinal);
    }

    private static string HashedProviderName(string baseName, string canonicalName)
    {
        var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonicalName)))
            .ToLowerInvariant();
        return baseName[..Math.Min(30, baseName.Length)] + "__" + hash[..32];
    }

    private static ToolAuthorityPolicy[] BuildAuthority(ToolsConfig config) =>
    [
        new(ToolEffect.None, ToolAuthorityDecision.Allow),
        new(ToolEffect.WorkspaceRead, ToolAuthorityDecision.Allow),
        new(ToolEffect.WorkspaceWrite, config.Effects.WorkspaceWrite),
        new(ToolEffect.ProcessExecution, config.Effects.ProcessExecution),
        new(ToolEffect.NetworkRead, config.Effects.NetworkRead),
        new(
            ToolEffect.ExternalMutation,
            config.Effects.ExternalMutation == ToolAuthorityDecision.Allow
                ? ToolAuthorityDecision.RequireApproval
                : config.Effects.ExternalMutation),
    ];

    private static ToolAuthorityDecision DecisionFor(
        ToolEffect effects,
        IReadOnlyList<ToolAuthorityPolicy> authority)
    {
        var decision = ToolAuthorityDecision.Allow;
        foreach (var policy in authority)
        {
            if (policy.Effect == ToolEffect.None
                ? effects == ToolEffect.None
                : (effects & policy.Effect) != 0)
            {
                decision = (ToolAuthorityDecision)Math.Min(
                    (int)decision,
                    (int)policy.Decision);
            }
        }

        return decision;
    }

    private static bool TryCompileSchema(
        ToolDefinition definition,
        out JsonSchema? schema)
    {
        schema = null;
        try
        {
            var schemaBytes = JsonSerializer.SerializeToUtf8Bytes(definition.InputSchema);
            if (schemaBytes.Length > ToolRuntimeLimits.MaximumSchemaBytes ||
                definition.InputSchema.ValueKind != JsonValueKind.Object ||
                !definition.InputSchema.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String ||
                !string.Equals(type.GetString(), "object", StringComparison.Ordinal) ||
                !definition.InputSchema.TryGetProperty(
                    "additionalProperties",
                    out var additionalProperties) ||
                additionalProperties.ValueKind is not JsonValueKind.False ||
                !HasSupportedDialect(definition.InputSchema))
            {
                return false;
            }

            schema = JsonSchema.Build(
                definition.InputSchema,
                new BuildOptions
                {
                    Dialect = Dialect.Draft202012,
                    SchemaRegistry = CreateClosedSchemaRegistry(),
                },
                new Uri(
                    "urn:opencowork:tool-schema:" +
                    Convert.ToHexString(SHA256.HashData(schemaBytes)).ToLowerInvariant()));
            return ValidateSchemaNode(schema.Root, new HashSet<JsonSchemaNode>());
        }
        catch (Exception exception) when (
            exception is JsonException or JsonSchemaException or
                ArgumentException or InvalidOperationException or FormatException)
        {
            schema = null;
            return false;
        }
    }

    private static bool HasSupportedDialect(JsonElement schema)
    {
        if (!schema.TryGetProperty("$schema", out var dialect))
        {
            return true;
        }

        return dialect.ValueKind == JsonValueKind.String &&
               Uri.TryCreate(dialect.GetString(), UriKind.Absolute, out var uri) &&
               uri == Draft202012Uri;
    }

    private static SchemaRegistry CreateClosedSchemaRegistry()
    {
        var registry = new SchemaRegistry();
        registry.Fetch = static (_, _) => throw new JsonSchemaException(
            "External schema resolution is disabled.");
        return registry;
    }

    private static bool ValidateSchemaNode(
        JsonSchemaNode node,
        HashSet<JsonSchemaNode> visited)
    {
        if (!visited.Add(node))
        {
            return true;
        }

        foreach (var keyword in node.Keywords)
        {
            if (keyword.Handler is AnnotationKeyword)
            {
                return false;
            }

            if (keyword.Handler.Name == "$schema" &&
                (keyword.RawValue.ValueKind != JsonValueKind.String ||
                 !Uri.TryCreate(
                     keyword.RawValue.GetString(),
                     UriKind.Absolute,
                     out var dialect) ||
                 dialect != Draft202012Uri))
            {
                return false;
            }

            if (keyword.Handler.Name is "$ref" or "$dynamicRef")
            {
                if (keyword.RawValue.ValueKind != JsonValueKind.String ||
                    keyword.RawValue.GetString() is not { } reference ||
                    !reference.StartsWith('#'))
                {
                    return false;
                }
            }

            if (keyword.Handler.Name == "$vocabulary" &&
                (keyword.RawValue.ValueKind != JsonValueKind.Object ||
                 keyword.RawValue.EnumerateObject().Any(
                     item => !AllowedVocabularies.Contains(item.Name))))
            {
                return false;
            }

            foreach (var subschema in keyword.Subschemas)
            {
                if (!ValidateSchemaNode(subschema, visited))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static byte[] WriteSnapshot(
        AgentMode mode,
        IReadOnlyList<ToolAuthorityPolicy> authority,
        IReadOnlyList<ToolRegistration> registrations,
        IReadOnlyDictionary<string, string> canonicalToProvider,
        IReadOnlyDictionary<string, string> providerToCanonical,
        IReadOnlyList<ToolSnapshotDiagnostic> diagnostics,
        string? snapshotSha256)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", SnapshotSchemaVersion);
        writer.WriteString("effectiveAgentMode", EnumText(mode));
        writer.WritePropertyName("authority");
        writer.WriteStartArray();
        foreach (var policy in authority)
        {
            writer.WriteStartObject();
            writer.WriteNumber("effect", (int)policy.Effect);
            writer.WriteString("decision", EnumText(policy.Decision));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WritePropertyName("registrations");
        writer.WriteStartArray();
        foreach (var registration in registrations)
        {
            var definition = registration.Definition;
            writer.WriteStartObject();
            writer.WriteString("sourceKind", EnumText(definition.Id.SourceKind));
            writer.WriteString("sourceId", definition.Id.SourceId);
            writer.WriteString("sourceToolId", definition.Id.SourceToolId);
            writer.WriteString("namespace", definition.Name.Namespace);
            writer.WriteString("name", definition.Name.Name);
            writer.WriteString("description", definition.Description);
            writer.WritePropertyName("inputSchema");
            WriteCanonicalElement(writer, definition.InputSchema);
            writer.WriteNumber("effects", (int)definition.Effects);
            writer.WriteString("replaySafety", EnumText(definition.ReplaySafety));
            writer.WriteString("runtimeBindingId", registration.RuntimeBindingId.Value);
            writer.WriteNumber("bindingGeneration", registration.BindingGeneration);
            writer.WriteString("exposure", EnumText(registration.Exposure));
            writer.WriteNumber("audience", (int)registration.Audience);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        WriteMap(writer, "canonicalToProviderNames", canonicalToProvider);
        WriteMap(writer, "providerToCanonicalNames", providerToCanonical);
        writer.WritePropertyName("diagnostics");
        writer.WriteStartArray();
        foreach (var diagnostic in diagnostics)
        {
            writer.WriteStartObject();
            writer.WriteString("code", diagnostic.Code);
            if (diagnostic.DefinitionId is { } id)
            {
                writer.WriteString("sourceKind", EnumText(id.SourceKind));
                writer.WriteString("sourceId", id.SourceId);
                writer.WriteString("sourceToolId", id.SourceToolId);
            }

            if (diagnostic.CanonicalName is not null)
            {
                writer.WriteString("canonicalName", diagnostic.CanonicalName);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        if (snapshotSha256 is not null)
        {
            writer.WriteString("snapshotSha256", snapshotSha256);
        }

        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteMap(
        Utf8JsonWriter writer,
        string propertyName,
        IReadOnlyDictionary<string, string> values)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartObject();
        foreach (var pair in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            writer.WriteString(pair.Key, pair.Value);
        }

        writer.WriteEndObject();
    }

    private static void WriteCanonicalElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element
                             .EnumerateObject()
                             .OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalElement(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalElement(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static CoreTools CreateCoreTools(
        CoreFileTools? fileTools,
        CoreShellTool? shellTool,
        CoreWebTool? webTool)
    {
        var registrations = new List<ToolRegistration>();
        var bindings = new List<ToolRuntimeBinding>();
        Add(
            "file.list",
            new ToolName("file", "list"),
            "List one workspace directory level.",
            """
            {
              "$schema":"https://json-schema.org/draft/2020-12/schema",
              "$defs":{"path":{"type":"string","minLength":1}},
              "type":"object",
              "properties":{"path":{"$ref":"#/$defs/path"}},
              "required":["path"],
              "additionalProperties":false
            }
            """,
            ToolEffect.WorkspaceRead,
            ToolReplaySafety.Safe,
            TimeSpan.FromSeconds(30),
            fileTools is null
                ? PlaceholderExecutor
                : fileTools.ListAsync);
        Add(
            "file.read",
            new ToolName("file", "read"),
            "Read strict UTF-8 text from a workspace file.",
            """
            {
              "$schema":"https://json-schema.org/draft/2020-12/schema",
              "$defs":{"path":{"type":"string","minLength":1}},
              "type":"object",
              "properties":{
                "path":{"$ref":"#/$defs/path"},
                "startLine":{"type":"integer","minimum":1},
                "lineCount":{"type":"integer","minimum":1}
              },
              "required":["path"],
              "additionalProperties":false
            }
            """,
            ToolEffect.WorkspaceRead,
            ToolReplaySafety.Safe,
            TimeSpan.FromSeconds(30),
            fileTools is null
                ? PlaceholderExecutor
                : fileTools.ReadAsync);
        Add(
            "file.write",
            new ToolName("file", "write"),
            "Atomically write a complete UTF-8 workspace file.",
            """
            {
              "$schema":"https://json-schema.org/draft/2020-12/schema",
              "$defs":{"path":{"type":"string","minLength":1}},
              "type":"object",
              "properties":{
                "path":{"$ref":"#/$defs/path"},
                "content":{"type":"string"},
                "expectedSha256":{"type":"string","pattern":"^[0-9a-f]{64}$"}
              },
              "required":["path","content"],
              "additionalProperties":false
            }
            """,
            ToolEffect.WorkspaceRead | ToolEffect.WorkspaceWrite,
            ToolReplaySafety.Unsafe,
            TimeSpan.FromSeconds(30),
            fileTools is null
                ? PlaceholderExecutor
                : fileTools.WriteAsync);
        Add(
            "shell.run",
            new ToolName("shell", "run"),
            "Run one non-interactive shell command in the workspace.",
            """
            {
              "$schema":"https://json-schema.org/draft/2020-12/schema",
              "type":"object",
              "properties":{
                "command":{"type":"string","minLength":1},
                "workingDirectory":{"type":"string","minLength":1}
              },
              "required":["command"],
              "additionalProperties":false
            }
            """,
            ToolEffect.WorkspaceRead |
            ToolEffect.WorkspaceWrite |
            ToolEffect.ProcessExecution |
            ToolEffect.NetworkRead |
            ToolEffect.ExternalMutation,
            ToolReplaySafety.Unsafe,
            TimeSpan.FromMinutes(10),
            shellTool is null
                ? PlaceholderExecutor
                : shellTool.RunAsync);
        Add(
            "web.fetch",
            new ToolName("web", "fetch"),
            "Fetch an unauthenticated HTTP or HTTPS text resource.",
            """
            {
              "$schema":"https://json-schema.org/draft/2020-12/schema",
              "type":"object",
              "properties":{
                "url":{"type":"string","minLength":1},
                "method":{"type":"string","enum":["GET","HEAD"]}
              },
              "required":["url"],
              "additionalProperties":false
            }
            """,
            ToolEffect.NetworkRead,
            ToolReplaySafety.Unsafe,
            TimeSpan.FromMinutes(2),
            webTool is null
                ? PlaceholderExecutor
                : webTool.FetchAsync);
        return new CoreTools(registrations, bindings);

        void Add(
            string canonicalName,
            ToolName name,
            string description,
            string schemaJson,
            ToolEffect effects,
            ToolReplaySafety replaySafety,
            TimeSpan timeout,
            ToolExecutor executor)
        {
            using var document = JsonDocument.Parse(schemaJson);
            var definition = new ToolDefinition(
                new ToolDefinitionId(
                    ToolSourceKind.CoreNative,
                    "opencowork.core",
                    canonicalName),
                name,
                description,
                document.RootElement,
                effects,
                replaySafety);
            var bindingId = new RuntimeBindingId($"core.{canonicalName}.v1");
            registrations.Add(new ToolRegistration(
                definition,
                bindingId,
                ToolExposure.Direct,
                ToolInvocationAudience.Model));
            bindings.Add(new ToolRuntimeBinding(
                bindingId,
                ToolBindingAvailability.Available,
                Lease: null,
                timeout,
                executor));
        }
    }

    private static ValueTask<ToolBindingResult> PlaceholderExecutor(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var document = JsonDocument.Parse("""{"status":"registered"}""");
        return ValueTask.FromResult(ToolBindingResult.Success(document.RootElement));
    }

    private static IEnumerable<Candidate> Ordered(IEnumerable<Candidate> candidates) =>
        candidates
            .OrderBy(
                candidate => CanonicalName(candidate.Registration.Definition.Name),
                StringComparer.Ordinal)
            .ThenBy(
                candidate => DefinitionKey(candidate.Registration.Definition.Id),
                StringComparer.Ordinal)
            .ThenBy(
                candidate => candidate.Registration.RuntimeBindingId.Value,
                StringComparer.Ordinal);

    private static ToolSnapshotDiagnostic Diagnostic(
        string code,
        ToolDefinitionId definitionId,
        string canonicalName) =>
        new(code, definitionId, canonicalName);

    private static string CanonicalName(ToolName name) =>
        $"{name.Namespace}.{name.Name}";

    private static string DefinitionKey(ToolDefinitionId? id) =>
        id is null
            ? string.Empty
            : $"{id.SourceKind}\0{id.SourceId}\0{id.SourceToolId}";

    private static bool IsCleanIdentity(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Any(char.IsControl);

    private static string EnumText<T>(T value) where T : struct, Enum =>
        JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePartPattern();

    private sealed record Candidate(
        ToolRegistration Registration,
        JsonSchema? Schema);

    private sealed record Projection(
        Candidate Candidate,
        string CanonicalName,
        string BaseName);

    private sealed record CoreTools(
        IReadOnlyList<ToolRegistration> Registrations,
        IReadOnlyList<ToolRuntimeBinding> Bindings);
}

internal sealed class ToolRuntimeException(string code, string message)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
