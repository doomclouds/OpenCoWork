using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cronos;
using Fluid;
using Fluid.Values;
using OpenCoWork.Abstractions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace OpenCoWork.Automations;

internal static class AutomationDefinitionDiagnosticCodes
{
    public const string InvalidYaml = "automation.definition.invalidYaml";
    public const string UnsupportedYaml = "automation.definition.unsupportedYaml";
    public const string InvalidSchema = "automation.definition.invalidSchema";
    public const string IdentityMismatch = "automation.definition.identityMismatch";
    public const string InvalidWorkspace = "automation.definition.invalidWorkspace";
    public const string InvalidSchedule = "automation.definition.invalidSchedule";
    public const string InvalidInputs = "automation.inputs.invalid";
    public const string TemplateInvalid = "automation.template.invalid";
    public const string TemplateRenderFailed = "automation.template.renderFailed";
    public const string SecretDetected = "automation.secretDetected";
    public const string LimitExceeded = "automation.limitExceeded";
}

internal sealed record AutomationScheduleCandidate(string Cron, string TimeZone);

internal sealed record AutomationWorkspaceCandidate(
    AutomationWorkspaceMode Mode,
    bool AllowDirtyOrigin);

internal sealed record AutomationAllowCandidate(
    IReadOnlyList<string> Plugins,
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> Tools,
    IReadOnlyList<string> Effects);

internal sealed record AutomationDefinitionCandidate(
    string Id,
    string DisplayName,
    string? Description,
    bool Enabled,
    AutomationScheduleCandidate? Schedule,
    AutomationWorkspaceCandidate Workspace,
    string Prompt,
    JsonElement InputSchema,
    JsonElement Defaults,
    AutomationAllowCandidate Allow,
    TimeSpan RunTimeout,
    TimeSpan AttentionTimeout,
    string DefinitionVersion,
    JsonElement CanonicalDefinition,
    IFluidTemplate Template);

internal sealed record AutomationDefinitionLoadResult(
    string SourceSha256,
    string? DefinitionVersion,
    AutomationDefinitionCandidate? Definition,
    IReadOnlyList<OpenCoWorkDiagnostic> Diagnostics)
{
    public bool IsValid => Definition is not null && Diagnostics.Count == 0;
}

internal sealed record AutomationTriggerContext(
    string Kind,
    DateTimeOffset? ScheduledForUtc);

internal sealed record AutomationTemplateRenderResult(
    string? Prompt,
    JsonElement? Inputs,
    IReadOnlyList<OpenCoWorkDiagnostic> Diagnostics)
{
    public bool IsValid => Prompt is not null && Inputs is not null && Diagnostics.Count == 0;
}

internal sealed partial class AutomationDefinitionLoader(
    IJsonSchemaValidationService schemas,
    ISensitiveDataService sensitiveData)
{
    private static readonly FluidParser TemplateParser = new();
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithDuplicateKeyChecking()
        .WithAttemptingUnquotedStringTypeDeserialization()
        .Build();

    public AutomationDefinitionLoadResult Load(
        string fileName,
        ReadOnlySpan<byte> source)
    {
        var sourceSha256 = Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();
        try
        {
            if (source.Length > AutomationRuntimeLimits.MaximumDefinitionBytes)
            {
                throw Invalid(
                    AutomationDefinitionDiagnosticCodes.LimitExceeded,
                    "Definition exceeds the fixed size limit.");
            }

            ValidateFileName(fileName);
            var yaml = new UTF8Encoding(false, true).GetString(source);
            ValidateYamlEvents(yaml);
            var raw = Deserializer.Deserialize<RawDefinition>(yaml)
                      ?? throw Invalid(
                          AutomationDefinitionDiagnosticCodes.InvalidYaml,
                          "Definition root is required.");
            var definition = Normalize(fileName, raw);
            return new AutomationDefinitionLoadResult(
                sourceSha256,
                definition.DefinitionVersion,
                definition,
                []);
        }
        catch (DefinitionValidationException exception)
        {
            return InvalidResult(sourceSha256, exception.Code, exception.Path);
        }
        catch (Exception exception) when (
            exception is YamlException or DecoderFallbackException or
                InvalidCastException or FormatException or OverflowException)
        {
            return InvalidResult(
                sourceSha256,
                AutomationDefinitionDiagnosticCodes.InvalidYaml,
                path: null);
        }
    }

    public AutomationDefinitionCandidate Hydrate(
        JsonElement canonical,
        string definitionVersion)
    {
        try
        {
            var canonicalBytes = CanonicalJson.Write(canonical);
            var actualVersion =
                Convert.ToHexString(SHA256.HashData(canonicalBytes)).ToLowerInvariant();
            if (!string.Equals(
                    actualVersion,
                    definitionVersion,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Automation Definition snapshot digest is invalid.");
            }

            var prompt = canonical.GetProperty("prompt").GetString()
                         ?? throw new InvalidDataException(
                             "Automation Definition prompt is invalid.");
            if (ContainsExternalTemplate(prompt) ||
                !TemplateParser.TryParse(prompt, out var template, out _))
            {
                throw new InvalidDataException(
                    "Automation Definition template is invalid.");
            }

            var workspace = canonical.GetProperty("workspace");
            var scheduleElement = canonical.GetProperty("schedule");
            var allow = canonical.GetProperty("allow");
            var inputSchema = canonical.GetProperty("inputSchema").Clone();
            var defaults = canonical.GetProperty("defaults").Clone();
            if (!schemas.IsValidSchema(inputSchema) ||
                !schemas.Evaluate(inputSchema, defaults))
            {
                throw new InvalidDataException(
                    "Automation Definition input schema is invalid.");
            }

            var candidate = new AutomationDefinitionCandidate(
                canonical.GetProperty("id").GetString()!,
                canonical.GetProperty("displayName").GetString()!,
                canonical.GetProperty("description").ValueKind == JsonValueKind.Null
                    ? null
                    : canonical.GetProperty("description").GetString(),
                canonical.GetProperty("enabled").GetBoolean(),
                scheduleElement.ValueKind == JsonValueKind.Null
                    ? null
                    : new AutomationScheduleCandidate(
                        scheduleElement.GetProperty("cron").GetString()!,
                        scheduleElement.GetProperty("timeZone").GetString()!),
                new AutomationWorkspaceCandidate(
                    workspace.GetProperty("mode").GetString() == "project"
                        ? AutomationWorkspaceMode.Project
                        : AutomationWorkspaceMode.Worktree,
                    workspace.GetProperty("allowDirtyOrigin").GetBoolean()),
                prompt,
                inputSchema,
                defaults,
                new AutomationAllowCandidate(
                    Strings(allow, "plugins"),
                    Strings(allow, "skills"),
                    Strings(allow, "tools"),
                    Strings(allow, "effects")),
                TimeSpan.FromMilliseconds(
                    canonical.GetProperty("runTimeoutMilliseconds").GetInt64()),
                TimeSpan.FromMilliseconds(
                    canonical.GetProperty("attentionTimeoutMilliseconds").GetInt64()),
                definitionVersion,
                canonical.Clone(),
                template!);
            if (string.IsNullOrWhiteSpace(candidate.Id) ||
                string.IsNullOrWhiteSpace(candidate.DisplayName))
            {
                throw new InvalidDataException(
                    "Automation Definition snapshot is invalid.");
            }

            return candidate;
        }
        catch (Exception exception) when (
            exception is KeyNotFoundException or InvalidOperationException or
                FormatException or OverflowException or ArgumentException)
        {
            throw new InvalidDataException(
                "Automation Definition snapshot is invalid.",
                exception);
        }
    }

    private AutomationDefinitionCandidate Normalize(
        string fileName,
        RawDefinition raw)
    {
        if (raw.SchemaVersion != 1)
        {
            throw Invalid(
                AutomationDefinitionDiagnosticCodes.InvalidSchema,
                "schemaVersion must be integer 1.",
                "schemaVersion");
        }

        var id = Required(raw.Id, "id");
        if (id.Length > 64 ||
            !AutomationIdPattern().IsMatch(id) ||
            !string.Equals(
                Path.GetFileNameWithoutExtension(fileName),
                id,
                StringComparison.Ordinal))
        {
            throw Invalid(
                AutomationDefinitionDiagnosticCodes.IdentityMismatch,
                "Definition ID must match its lower-kebab-case file name.",
                "id");
        }

        var displayName = Required(raw.DisplayName, "displayName");
        if (raw.Enabled is null || raw.Workspace is null || raw.Allow is null)
        {
            throw Invalid(
                AutomationDefinitionDiagnosticCodes.InvalidSchema,
                "Required Definition fields are missing.");
        }

        var prompt = Required(raw.Prompt, "prompt");
        if (ContainsExternalTemplate(prompt) ||
            !TemplateParser.TryParse(prompt, out var template, out _))
        {
            throw Invalid(
                AutomationDefinitionDiagnosticCodes.TemplateInvalid,
                "Prompt template is invalid.",
                "prompt");
        }

        var workspaceMode = raw.Workspace.Mode switch
        {
            "project" => AutomationWorkspaceMode.Project,
            "worktree" => AutomationWorkspaceMode.Worktree,
            _ => throw Invalid(
                AutomationDefinitionDiagnosticCodes.InvalidWorkspace,
                "Workspace mode must be project or worktree.",
                "workspace.mode"),
        };
        var allowDirty = raw.Workspace.AllowDirtyOrigin ?? false;
        if (workspaceMode == AutomationWorkspaceMode.Project && allowDirty)
        {
            throw Invalid(
                AutomationDefinitionDiagnosticCodes.InvalidWorkspace,
                "allowDirtyOrigin is only valid for worktree mode.",
                "workspace.allowDirtyOrigin");
        }

        AutomationScheduleCandidate? schedule = null;
        if (raw.Schedule is not null)
        {
            var cron = Required(raw.Schedule.Cron, "schedule.cron");
            var timeZone = Required(raw.Schedule.TimeZone, "schedule.timeZone");
            try
            {
                if (timeZone != "UTC" && !timeZone.Contains('/'))
                {
                    throw new TimeZoneNotFoundException();
                }

                _ = CronExpression.Parse(cron, CronFormat.Standard);
                _ = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
            }
            catch (Exception exception) when (
                exception is CronFormatException or TimeZoneNotFoundException or
                    InvalidTimeZoneException)
            {
                throw Invalid(
                    AutomationDefinitionDiagnosticCodes.InvalidSchedule,
                    "Schedule must use a five-field Cron and explicit IANA time zone.",
                    "schedule");
            }

            schedule = new AutomationScheduleCandidate(cron, timeZone);
        }

        var inputSchema = Json(raw.InputSchema ?? new Dictionary<object, object>());
        if (!schemas.IsValidSchema(inputSchema))
        {
            throw Invalid(
                AutomationDefinitionDiagnosticCodes.InvalidSchema,
                "inputSchema is invalid.",
                "inputSchema");
        }

        var defaults = Json(raw.Defaults ?? new Dictionary<object, object>());
        if (defaults.ValueKind != JsonValueKind.Object ||
            !schemas.Evaluate(inputSchema, defaults))
        {
            throw Invalid(
                AutomationDefinitionDiagnosticCodes.InvalidSchema,
                "defaults must be a valid input object.",
                "defaults");
        }

        var runTimeout = Duration(
            raw.RunTimeout,
            AutomationRuntimeLimits.DefaultRunTimeout,
            AutomationRuntimeLimits.MinimumRunTimeout,
            AutomationRuntimeLimits.MaximumRunTimeout,
            "runTimeout");
        var attentionTimeout = Duration(
            raw.AttentionTimeout,
            AutomationRuntimeLimits.DefaultAttentionTimeout,
            AutomationRuntimeLimits.MinimumAttentionTimeout,
            AutomationRuntimeLimits.MaximumAttentionTimeout,
            "attentionTimeout");
        var allow = new AutomationAllowCandidate(
            CleanList(raw.Allow.Plugins, "allow.plugins"),
            CleanList(raw.Allow.Skills, "allow.skills"),
            CleanList(raw.Allow.Tools, "allow.tools"),
            CleanEffects(raw.Allow.Effects));
        var canonical = JsonSerializer.SerializeToElement(new
        {
            schemaVersion = 1,
            id,
            displayName,
            description = raw.Description?.Trim(),
            enabled = raw.Enabled.Value,
            schedule,
            workspace = new
            {
                mode = workspaceMode == AutomationWorkspaceMode.Project
                    ? "project"
                    : "worktree",
                allowDirtyOrigin = allowDirty,
            },
            prompt,
            inputSchema,
            defaults,
            allow,
            runTimeoutMilliseconds = (long)runTimeout.TotalMilliseconds,
            attentionTimeoutMilliseconds = (long)attentionTimeout.TotalMilliseconds,
        }, JsonOptions);
        var canonicalBytes = CanonicalJson.Write(canonical);
        var definitionVersion =
            Convert.ToHexString(SHA256.HashData(canonicalBytes)).ToLowerInvariant();
        return new AutomationDefinitionCandidate(
            id,
            displayName,
            raw.Description?.Trim(),
            raw.Enabled.Value,
            schedule,
            new AutomationWorkspaceCandidate(workspaceMode, allowDirty),
            prompt,
            inputSchema,
            defaults,
            allow,
            runTimeout,
            attentionTimeout,
            definitionVersion,
            JsonDocument.Parse(canonicalBytes).RootElement.Clone(),
            template!);
    }

    private static void ValidateFileName(string fileName)
    {
        if (!string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal) ||
            !string.Equals(Path.GetExtension(fileName), ".yaml", StringComparison.Ordinal))
        {
            throw Invalid(
                AutomationDefinitionDiagnosticCodes.IdentityMismatch,
                "Definition must be a direct .yaml file.");
        }
    }

    private static void ValidateYamlEvents(string yaml)
    {
        var parser = new Parser(new StringReader(yaml));
        var nodes = 0;
        var depth = 0;
        var schemaVersionValue = false;
        while (parser.MoveNext())
        {
            if (schemaVersionValue)
            {
                if (parser.Current is not Scalar
                    {
                        Style: ScalarStyle.Plain,
                        Value: "1",
                    })
                {
                    throw Invalid(
                        AutomationDefinitionDiagnosticCodes.InvalidSchema,
                        "schemaVersion must be integer 1.",
                        "schemaVersion");
                }

                schemaVersionValue = false;
            }

            if (parser.Current is AnchorAlias)
            {
                throw Invalid(
                    AutomationDefinitionDiagnosticCodes.UnsupportedYaml,
                    "YAML aliases are disabled.");
            }

            if (parser.Current is NodeEvent node &&
                (!node.Anchor.IsEmpty ||
                 !node.Tag.IsEmpty && !node.Tag.IsNonSpecific))
            {
                throw Invalid(
                    AutomationDefinitionDiagnosticCodes.UnsupportedYaml,
                    "YAML anchors and custom tags are disabled.");
            }

            if (parser.Current is Scalar or MappingStart or SequenceStart)
            {
                nodes++;
                if (nodes > AutomationRuntimeLimits.MaximumDocumentNodes)
                {
                    throw Invalid(
                        AutomationDefinitionDiagnosticCodes.LimitExceeded,
                        "YAML node limit exceeded.");
                }
            }

            if (parser.Current is Scalar { Value: "schemaVersion" } && depth == 1)
            {
                schemaVersionValue = true;
            }

            if (parser.Current is MappingStart or SequenceStart)
            {
                depth++;
                if (depth > AutomationRuntimeLimits.MaximumDocumentDepth)
                {
                    throw Invalid(
                        AutomationDefinitionDiagnosticCodes.LimitExceeded,
                        "YAML depth limit exceeded.");
                }
            }
            else if (parser.Current is MappingEnd or SequenceEnd)
            {
                depth--;
            }
        }
    }

    private AutomationDefinitionLoadResult InvalidResult(
        string sourceSha256,
        string code,
        string? path)
    {
        const string message = "Automation Definition is invalid.";
        return new AutomationDefinitionLoadResult(
            sourceSha256,
            null,
            null,
            [
                new OpenCoWorkDiagnostic(
                    code,
                    OpenCoWorkDiagnosticSeverity.Error,
                    sensitiveData.Redact(message),
                    path),
            ]);
    }

    private static JsonElement Json(object value)
    {
        var node = ToJsonNode(value, 1, new NodeCounter());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(node);
        if (bytes.Length > AutomationRuntimeLimits.MaximumInputBytes)
        {
            throw Invalid(
                AutomationDefinitionDiagnosticCodes.LimitExceeded,
                "JSON value exceeds the fixed size limit.");
        }

        return JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            MaxDepth = AutomationRuntimeLimits.MaximumDocumentDepth,
        }).RootElement.Clone();
    }

    private static string[] Strings(JsonElement value, string property) =>
        value.GetProperty(property)
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();

    private static JsonNode? ToJsonNode(object? value, int depth, NodeCounter counter)
    {
        if (depth > AutomationRuntimeLimits.MaximumDocumentDepth ||
            ++counter.Value > AutomationRuntimeLimits.MaximumDocumentNodes)
        {
            throw Invalid(
                AutomationDefinitionDiagnosticCodes.LimitExceeded,
                "JSON structure limit exceeded.");
        }

        return value switch
        {
            null => null,
            string text => JsonValue.Create(text),
            bool boolean => JsonValue.Create(boolean),
            byte number => JsonValue.Create(number),
            short number => JsonValue.Create(number),
            int number => JsonValue.Create(number),
            long number => JsonValue.Create(number),
            float number when float.IsFinite(number) => JsonValue.Create(number),
            double number when double.IsFinite(number) => JsonValue.Create(number),
            decimal number => JsonValue.Create(number),
            IDictionary<object, object> map => Object(map, depth, counter),
            IEnumerable<object> items => new JsonArray(
                items.Select(item => ToJsonNode(item, depth + 1, counter)).ToArray()),
            _ => throw Invalid(
                AutomationDefinitionDiagnosticCodes.InvalidSchema,
                "Only JSON-compatible YAML values are allowed."),
        };
    }

    private static JsonObject Object(
        IDictionary<object, object> map,
        int depth,
        NodeCounter counter)
    {
        var result = new JsonObject();
        foreach (var (key, value) in map)
        {
            if (key is not string name)
            {
                throw Invalid(
                    AutomationDefinitionDiagnosticCodes.InvalidSchema,
                    "JSON object keys must be strings.");
            }

            result.Add(name, ToJsonNode(value, depth + 1, counter));
        }

        return result;
    }

    private static TimeSpan Duration(
        string? text,
        TimeSpan fallback,
        TimeSpan minimum,
        TimeSpan maximum,
        string path)
    {
        if (text is null)
        {
            return fallback;
        }

        var match = DurationPattern().Match(text);
        if (!match.Success ||
            !long.TryParse(
                match.Groups[1].Value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var number))
        {
            throw Invalid(
                AutomationDefinitionDiagnosticCodes.InvalidSchema,
                "Duration is invalid.",
                path);
        }

        TimeSpan value;
        try
        {
            value = match.Groups[2].Value switch
            {
                "s" => TimeSpan.FromSeconds(number),
                "m" => TimeSpan.FromMinutes(number),
                "h" => TimeSpan.FromHours(number),
                "d" => TimeSpan.FromDays(number),
                _ => throw new UnreachableException(),
            };
        }
        catch (OverflowException)
        {
            throw Invalid(
                AutomationDefinitionDiagnosticCodes.InvalidSchema,
                "Duration is invalid.",
                path);
        }

        if (value < minimum || value > maximum)
        {
            throw Invalid(
                AutomationDefinitionDiagnosticCodes.InvalidSchema,
                "Duration is outside the supported range.",
                path);
        }

        return value;
    }

    private static IReadOnlyList<string> CleanList(
        IReadOnlyList<string>? values,
        string path)
    {
        var result = (values ?? [])
            .Select(value => value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if ((values?.Count ?? 0) != result.Length)
        {
            throw Invalid(
                AutomationDefinitionDiagnosticCodes.InvalidSchema,
                "Allow entries must be unique non-empty strings.",
                path);
        }

        return result;
    }

    private static IReadOnlyList<string> CleanEffects(
        IReadOnlyList<string>? values)
    {
        var result = CleanList(values, "allow.effects");
        if (result.Any(value => value is not (
                "workspaceRead" or
                "workspaceWrite" or
                "processExecution" or
                "networkRead" or
                "externalMutation")))
        {
            throw Invalid(
                AutomationDefinitionDiagnosticCodes.InvalidSchema,
                "Automation effects are invalid.",
                "allow.effects");
        }

        return result;
    }

    private static string Required(string? value, string path) =>
        string.IsNullOrWhiteSpace(value)
            ? throw Invalid(
                AutomationDefinitionDiagnosticCodes.InvalidSchema,
                "Required text is missing.",
                path)
            : value.Trim();

    private static bool ContainsExternalTemplate(string prompt) =>
        Regex.IsMatch(
            prompt,
            """{%\s*(include|render)\b""",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static DefinitionValidationException Invalid(
        string code,
        string message,
        string? path = null) =>
        new(code, message, path);

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex AutomationIdPattern();

    [GeneratedRegex("^([1-9][0-9]*)(s|m|h|d)$", RegexOptions.CultureInvariant)]
    private static partial Regex DurationPattern();

    private sealed class NodeCounter
    {
        public int Value;
    }

    private sealed class DefinitionValidationException(
        string code,
        string message,
        string? path) : Exception(message)
    {
        public string Code { get; } = code;

        public string? Path { get; } = path;
    }

    private sealed class RawDefinition
    {
        public int? SchemaVersion { get; init; }
        public string? Id { get; init; }
        public string? DisplayName { get; init; }
        public string? Description { get; init; }
        public bool? Enabled { get; init; }
        public RawSchedule? Schedule { get; init; }
        public RawWorkspace? Workspace { get; init; }
        public string? Prompt { get; init; }
        public object? InputSchema { get; init; }
        public object? Defaults { get; init; }
        public RawAllow? Allow { get; init; }
        public string? RunTimeout { get; init; }
        public string? AttentionTimeout { get; init; }
    }

    private sealed class RawSchedule
    {
        public string? Cron { get; init; }
        public string? TimeZone { get; init; }
    }

    private sealed class RawWorkspace
    {
        public string? Mode { get; init; }
        public bool? AllowDirtyOrigin { get; init; }
    }

    private sealed class RawAllow
    {
        public List<string>? Plugins { get; init; }
        public List<string>? Skills { get; init; }
        public List<string>? Tools { get; init; }
        public List<string>? Effects { get; init; }
    }
}

internal sealed partial class AutomationTemplateRenderer(
    IJsonSchemaValidationService schemas,
    ISensitiveDataService sensitiveData,
    TimeProvider timeProvider)
{
    public async Task<AutomationTemplateRenderResult> RenderAsync(
        AutomationDefinitionCandidate definition,
        Guid runId,
        JsonElement manualInputs,
        AutomationTriggerContext trigger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(trigger);
        try
        {
            if (runId.Version != 7)
            {
                return Invalid(AutomationDefinitionDiagnosticCodes.InvalidInputs);
            }

            if (manualInputs.ValueKind != JsonValueKind.Object)
            {
                return Invalid(AutomationDefinitionDiagnosticCodes.InvalidInputs);
            }

            var inputs = Merge(definition.Defaults, manualInputs);
            var canonicalInputs = CanonicalJson.Write(inputs);
            if (canonicalInputs.Length > AutomationRuntimeLimits.MaximumInputBytes ||
                !schemas.Evaluate(definition.InputSchema, inputs))
            {
                return Invalid(AutomationDefinitionDiagnosticCodes.InvalidInputs);
            }

            ValidateReferences(definition, runId, inputs, trigger);
            var options = new TemplateOptions
            {
                MaxSteps = 100_000,
                Undefined = name => throw new InvalidOperationException(
                    $"Undefined template value: {name}"),
                Now = static () => DateTimeOffset.UnixEpoch,
            };
            var context = new TemplateContext(options)
            {
                AllowModelMembers = false,
            };
            context.SetValue("automation", Dictionary(new Dictionary<string, FluidValue>
            {
                ["id"] = new StringValue(definition.Id),
                ["displayName"] = new StringValue(definition.DisplayName),
                ["description"] = new StringValue(definition.Description ?? string.Empty),
                ["definitionVersion"] = new StringValue(definition.DefinitionVersion),
            }));
            context.SetValue("run", Dictionary(new Dictionary<string, FluidValue>
            {
                ["id"] = new StringValue(runId.ToString("D")),
            }));
            context.SetValue("inputs", Fluid(inputs));
            context.SetValue("trigger", Dictionary(new Dictionary<string, FluidValue>
            {
                ["kind"] = new StringValue(trigger.Kind),
                ["scheduledForUtc"] = new StringValue(
                    trigger.ScheduledForUtc?.ToString("O") ?? string.Empty),
            }));

            var renderTask = Task.Run(
                async () => await definition.Template.RenderAsync(context),
                cancellationToken);
            var prompt = await renderTask.WaitAsync(
                AutomationRuntimeLimits.RenderTimeout,
                timeProvider,
                cancellationToken);
            if (Encoding.UTF8.GetByteCount(prompt) >
                AutomationRuntimeLimits.MaximumRenderedPromptBytes)
            {
                return Invalid(AutomationDefinitionDiagnosticCodes.LimitExceeded);
            }

            if (sensitiveData.ContainsSensitiveData(prompt))
            {
                return Invalid(AutomationDefinitionDiagnosticCodes.SecretDetected);
            }

            return new AutomationTemplateRenderResult(
                prompt,
                JsonDocument.Parse(canonicalInputs).RootElement.Clone(),
                []);
        }
        catch (TimeoutException)
        {
            return Invalid(AutomationDefinitionDiagnosticCodes.TemplateRenderFailed);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ArgumentException or
                JsonException or ParseException)
        {
            return Invalid(AutomationDefinitionDiagnosticCodes.TemplateRenderFailed);
        }
    }

    private void ValidateReferences(
        AutomationDefinitionCandidate definition,
        Guid runId,
        JsonElement inputs,
        AutomationTriggerContext trigger)
    {
        var roots = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["automation"] = JsonSerializer.SerializeToElement(new
            {
                id = definition.Id,
                displayName = definition.DisplayName,
                description = definition.Description,
                definitionVersion = definition.DefinitionVersion,
            }),
            ["run"] = JsonSerializer.SerializeToElement(new
            {
                id = runId.ToString("D"),
            }),
            ["inputs"] = inputs,
            ["trigger"] = JsonSerializer.SerializeToElement(new
            {
                kind = trigger.Kind,
                scheduledForUtc = trigger.ScheduledForUtc?.ToString("O"),
            }),
        };
        var locals = LoopVariablePattern()
            .Matches(definition.Prompt)
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        foreach (Match match in ReferencePattern().Matches(definition.Prompt))
        {
            var path = match.Groups[1].Value;
            var parts = path.Split('.');
            if (locals.Contains(parts[0]))
            {
                if (parts.Length == 1)
                {
                    continue;
                }

                throw new InvalidOperationException("Loop object member access is denied.");
            }

            if (!roots.TryGetValue(parts[0], out var value) ||
                !TryResolve(value, parts.AsSpan(1)))
            {
                throw new InvalidOperationException("Template value is undefined.");
            }
        }

        foreach (Match match in LoopSourcePattern().Matches(definition.Prompt))
        {
            var parts = match.Groups[1].Value.Split('.');
            if (!roots.TryGetValue(parts[0], out var value) ||
                !TryResolve(value, parts.AsSpan(1)))
            {
                throw new InvalidOperationException("Template loop source is undefined.");
            }
        }
    }

    private static bool TryResolve(JsonElement value, ReadOnlySpan<string> parts)
    {
        foreach (var part in parts)
        {
            if (value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty(part, out value))
            {
                return false;
            }
        }

        return true;
    }

    private static JsonElement Merge(JsonElement defaults, JsonElement manual)
    {
        var target = JsonNode.Parse(defaults.GetRawText())!.AsObject();
        Merge(target, JsonNode.Parse(manual.GetRawText())!.AsObject());
        return JsonSerializer.SerializeToElement(target);
    }

    private static void Merge(JsonObject target, JsonObject overlay)
    {
        foreach (var (name, value) in overlay)
        {
            if (value is JsonObject overlayObject &&
                target[name] is JsonObject targetObject)
            {
                Merge(targetObject, overlayObject);
            }
            else
            {
                target[name] = value?.DeepClone();
            }
        }
    }

    private static FluidValue Fluid(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Object => Dictionary(value.EnumerateObject().ToDictionary(
                property => property.Name,
                property => Fluid(property.Value),
                StringComparer.Ordinal)),
            JsonValueKind.Array => new ArrayValue(
                value.EnumerateArray().Select(Fluid).ToArray()),
            JsonValueKind.String => new StringValue(value.GetString() ?? string.Empty),
            JsonValueKind.Number when value.TryGetDecimal(out var number) =>
                NumberValue.Create(number),
            JsonValueKind.True => BooleanValue.True,
            JsonValueKind.False => BooleanValue.False,
            JsonValueKind.Null => NilValue.Instance,
            _ => throw new InvalidOperationException("JSON value is not renderable."),
        };

    private static DictionaryValue Dictionary(
        IDictionary<string, FluidValue> values) =>
        new(new FluidValueDictionaryFluidIndexable(values));

    private static AutomationTemplateRenderResult Invalid(string code) =>
        new(
            null,
            null,
            [
                new OpenCoWorkDiagnostic(
                    code,
                    OpenCoWorkDiagnosticSeverity.Error,
                    "Automation trigger validation failed."),
            ]);

    [GeneratedRegex(
        """{{\s*([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)""",
        RegexOptions.CultureInvariant)]
    private static partial Regex ReferencePattern();

    [GeneratedRegex(
        """{%\s*for\s+([A-Za-z_][A-Za-z0-9_]*)\s+in\b""",
        RegexOptions.CultureInvariant)]
    private static partial Regex LoopVariablePattern();

    [GeneratedRegex(
        """{%\s*for\s+[A-Za-z_][A-Za-z0-9_]*\s+in\s+([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)""",
        RegexOptions.CultureInvariant)]
    private static partial Regex LoopSourcePattern();
}

internal static class CanonicalJson
{
    public static byte[] Write(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            Write(writer, value);
        }

        return stream.ToArray();
    }

    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject()
                             .OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    Write(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}
