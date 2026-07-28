using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Core.Configuration;

public enum ConfigSourceKind
{
    BuiltInDefault,
    UserFile,
    WorkspaceFile,
    LocalFile,
    ExplicitFile,
    Environment,
    SetOverride,
    DedicatedOption,
}

public sealed record ConfigValueSource(ConfigSourceKind Kind, string SourceId);

public sealed record ConfigLoadRequest(IReadOnlyList<ConfigSectionDescriptor> Sections)
{
    public string? UserConfigPath { get; init; }

    public string? WorkspaceConfigPath { get; init; }

    public string? LocalConfigPath { get; init; }

    public string? ExplicitConfigPath { get; init; }

    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyList<string> SetOverrides { get; init; } = [];

    public IReadOnlyDictionary<string, string> DedicatedOptions { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public bool Strict { get; init; }
}

public sealed record ConfigLoadResult(
    EffectiveConfigSnapshot? Snapshot,
    OpenCoWorkValidationResult Validation);

public sealed class EffectiveConfigSnapshot
{
    private readonly IReadOnlyDictionary<Type, JsonElement> _sections;
    private readonly string[] _secretValues;

    internal EffectiveConfigSnapshot(
        IReadOnlyDictionary<Type, JsonElement> sections,
        IReadOnlyDictionary<string, ConfigValueSource> sources,
        IEnumerable<string> secretValues)
    {
        _sections = sections.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Clone());
        Sources = new ReadOnlyDictionary<string, ConfigValueSource>(
            new Dictionary<string, ConfigValueSource>(sources, StringComparer.Ordinal));
        _secretValues = secretValues
            .Where(value => !string.IsNullOrEmpty(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyDictionary<string, ConfigValueSource> Sources { get; }

    public T GetRequiredSection<T>()
    {
        if (!_sections.TryGetValue(typeof(T), out var section))
        {
            throw new KeyNotFoundException(
                $"Configuration section for {typeof(T).FullName} is not registered.");
        }

        return section.Deserialize<T>(ConfigLoader.SerializerOptions)
            ?? throw new InvalidDataException(
                $"Configuration section for {typeof(T).FullName} could not be bound.");
    }

    public ConfigValueSource GetRequiredSource(string path)
    {
        return Sources.TryGetValue(path, out var source)
            ? source
            : throw new KeyNotFoundException($"Configuration source for '{path}' was not found.");
    }

    internal IReadOnlyList<string> GetSecretValues() => _secretValues;
}

public static class ConfigLoader
{
    private const string EnvironmentPrefix = "OPENCOWORK__";
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    internal static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = false,
        Converters =
        {
            new JsonStringEnumConverter<ToolAuthorityDecision>(JsonNamingPolicy.CamelCase),
            new DurationJsonConverter(),
        },
    };

    public static ConfigLoadResult Load(ConfigLoadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Sections);

        var diagnostics = new List<OpenCoWorkDiagnostic>();
        var sources = new Dictionary<string, ConfigValueSource>(StringComparer.Ordinal);
        var schemas = ParseSchemas(request.Sections, diagnostics);
        var root = BuildDefaults(request.Sections, sources, diagnostics);

        MergeFile(
            root,
            request.UserConfigPath,
            ConfigSourceKind.UserFile,
            required: false,
            sources,
            diagnostics);
        var userPolicyRoot = root.DeepClone().AsObject();
        MergeFile(
            root,
            request.WorkspaceConfigPath,
            ConfigSourceKind.WorkspaceFile,
            required: false,
            sources,
            diagnostics);
        ValidateToolWorkspaceNarrowing(userPolicyRoot, root, diagnostics);
        MergeFile(
            root,
            request.LocalConfigPath,
            ConfigSourceKind.LocalFile,
            required: false,
            sources,
            diagnostics);
        ValidateToolWorkspaceNarrowing(userPolicyRoot, root, diagnostics);
        MergeFile(
            root,
            request.ExplicitConfigPath,
            ConfigSourceKind.ExplicitFile,
            required: true,
            sources,
            diagnostics);

        foreach (var pair in request.Environment
                     .Where(pair => pair.Key.StartsWith(EnvironmentPrefix, StringComparison.Ordinal))
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var path = pair.Key[EnvironmentPrefix.Length..].Replace("__", ".", StringComparison.Ordinal);
            ApplyOverride(
                root,
                schemas,
                path,
                pair.Value,
                new ConfigValueSource(ConfigSourceKind.Environment, pair.Key),
                sources,
                diagnostics);
        }

        for (var index = 0; index < request.SetOverrides.Count; index++)
        {
            var item = request.SetOverrides[index];
            var separator = item.IndexOf('=');

            if (separator <= 0)
            {
                diagnostics.Add(Error(
                    "OCWCFG002",
                    $"--set[{index}] 必须使用 path=value 格式。",
                    null));
                continue;
            }

            ApplyOverride(
                root,
                schemas,
                item[..separator],
                item[(separator + 1)..],
                new ConfigValueSource(ConfigSourceKind.SetOverride, $"--set[{index}]"),
                sources,
                diagnostics);
        }

        foreach (var pair in request.DedicatedOptions.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            ApplyOverride(
                root,
                schemas,
                pair.Key,
                pair.Value,
                new ConfigValueSource(ConfigSourceKind.DedicatedOption, $"--{pair.Key}"),
                sources,
                diagnostics);
        }

        ValidateRoot(root, schemas, request.Strict, diagnostics);

        if (diagnostics.Any(item => item.Severity == OpenCoWorkDiagnosticSeverity.Error))
        {
            return new ConfigLoadResult(null, new OpenCoWorkValidationResult(diagnostics));
        }

        var sections = BindSections(root, request.Sections, diagnostics);

        if (diagnostics.Any(item => item.Severity == OpenCoWorkDiagnosticSeverity.Error))
        {
            return new ConfigLoadResult(null, new OpenCoWorkValidationResult(diagnostics));
        }

        var secrets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var descriptor in request.Sections)
        {
            if (root[descriptor.Name] is { } section &&
                schemas.TryGetValue(descriptor.Name, out var schema))
            {
                CollectSecrets(section, schema, secrets);
            }
        }

        return new ConfigLoadResult(
            new EffectiveConfigSnapshot(sections, sources, secrets),
            new OpenCoWorkValidationResult(diagnostics));
    }

    private static Dictionary<string, JsonObject> ParseSchemas(
        IReadOnlyList<ConfigSectionDescriptor> descriptors,
        List<OpenCoWorkDiagnostic> diagnostics)
    {
        var schemas = new Dictionary<string, JsonObject>(StringComparer.Ordinal);

        foreach (var descriptor in descriptors)
        {
            try
            {
                schemas.Add(
                    descriptor.Name,
                    JsonNode.Parse(descriptor.JsonSchema)?.AsObject()
                    ?? throw new JsonException("Schema root must be an object."));
            }
            catch (Exception exception) when (
                exception is JsonException or InvalidOperationException or ArgumentException)
            {
                diagnostics.Add(Error(
                    "OCWCFG005",
                    $"配置节 '{descriptor.Name}' 的 Generated Schema 无效：{exception.Message}",
                    descriptor.Name));
            }
        }

        return schemas;
    }

    private static void ValidateToolWorkspaceNarrowing(
        JsonObject userRoot,
        JsonObject workspaceRoot,
        List<OpenCoWorkDiagnostic> diagnostics)
    {
        foreach (var property in new[]
                 {
                     "networkRead",
                     "workspaceWrite",
                     "processExecution",
                     "externalMutation",
                 })
        {
            if (!TryReadToolDecision(userRoot, property, out var userDecision) ||
                !TryReadToolDecision(workspaceRoot, property, out var workspaceDecision) ||
                workspaceDecision <= userDecision)
            {
                continue;
            }

            var path = $"tools.effects.{property}";
            if (diagnostics.Any(item =>
                    item.Code == "OCWCFG009" &&
                    string.Equals(item.Path, path, StringComparison.Ordinal)))
            {
                continue;
            }

            diagnostics.Add(Error(
                "OCWCFG009",
                $"工作区工具策略 '{path}' 不能放宽用户级策略。",
                path));
        }
    }

    private static bool TryReadToolDecision(
        JsonObject root,
        string property,
        out ToolAuthorityDecision decision)
    {
        decision = default;
        return root["tools"] is JsonObject tools &&
               tools["effects"] is JsonObject effects &&
               effects[property] is JsonValue value &&
               value.TryGetValue<string>(out var text) &&
               Enum.TryParse(text, ignoreCase: true, out decision);
    }

    private static JsonObject BuildDefaults(
        IReadOnlyList<ConfigSectionDescriptor> descriptors,
        Dictionary<string, ConfigValueSource> sources,
        List<OpenCoWorkDiagnostic> diagnostics)
    {
        var root = new JsonObject();

        foreach (var descriptor in descriptors)
        {
            try
            {
                var value = JsonSerializer.SerializeToNode(
                    descriptor.CreateDefault(),
                    descriptor.SectionType,
                    SerializerOptions);
                root.Add(descriptor.Name, value);
                MarkLeaves(
                    value,
                    descriptor.Name,
                    new ConfigValueSource(ConfigSourceKind.BuiltInDefault, "built-in"),
                    sources);
            }
            catch (Exception exception) when (
                exception is JsonException or NotSupportedException or InvalidOperationException)
            {
                diagnostics.Add(Error(
                    "OCWCFG006",
                    $"配置节 '{descriptor.Name}' 的默认值无法序列化：{exception.Message}",
                    descriptor.Name));
            }
        }

        return root;
    }

    private static void MergeFile(
        JsonObject root,
        string? path,
        ConfigSourceKind kind,
        bool required,
        Dictionary<string, ConfigValueSource> sources,
        List<OpenCoWorkDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!File.Exists(path))
        {
            if (required)
            {
                diagnostics.Add(Error(
                    "OCWCFG001",
                    $"显式配置文件不存在：{path}",
                    null));
            }

            return;
        }

        try
        {
            var document = JsonNode.Parse(File.ReadAllText(path), null, DocumentOptions);
            if (document is not JsonObject fileRoot)
            {
                diagnostics.Add(Error(
                    "OCWCFG001",
                    $"配置文件根节点必须是 JSON 对象：{path}",
                    null));
                return;
            }

            MergeObjects(
                root,
                fileRoot,
                string.Empty,
                new ConfigValueSource(kind, Path.GetFullPath(path)),
                sources);
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(Error(
                "OCWCFG001",
                $"无法读取配置文件 '{path}'：{exception.Message}",
                null));
        }
    }

    private static void MergeObjects(
        JsonObject target,
        JsonObject source,
        string parentPath,
        ConfigValueSource valueSource,
        Dictionary<string, ConfigValueSource> sources)
    {
        foreach (var pair in source)
        {
            var path = JoinPath(parentPath, pair.Key);

            if (pair.Value is JsonObject sourceObject &&
                target[pair.Key] is JsonObject targetObject)
            {
                MergeObjects(targetObject, sourceObject, path, valueSource, sources);
                continue;
            }

            target[pair.Key] = pair.Value?.DeepClone();
            MarkLeaves(pair.Value, path, valueSource, sources);
        }
    }

    private static void ApplyOverride(
        JsonObject root,
        IReadOnlyDictionary<string, JsonObject> schemas,
        string path,
        string rawValue,
        ConfigValueSource source,
        Dictionary<string, ConfigValueSource> sources,
        List<OpenCoWorkDiagnostic> diagnostics)
    {
        if (!TryFindSchema(schemas, path, out _))
        {
            diagnostics.Add(Error(
                "OCWCFG002",
                $"配置路径 '{path}' 不存在或大小写不匹配。",
                path));
            return;
        }

        if (!TryParseOverrideValue(rawValue, out var value, out var error))
        {
            diagnostics.Add(Error("OCWCFG003", error!, path));
            return;
        }

        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        JsonObject current = root;

        for (var index = 0; index < segments.Length - 1; index++)
        {
            if (current[segments[index]] is not JsonObject child)
            {
                child = new JsonObject();
                current[segments[index]] = child;
            }

            current = child;
        }

        current[segments[^1]] = value;
        MarkLeaves(value, path, source, sources);
    }

    private static bool TryParseOverrideValue(
        string value,
        out JsonNode? node,
        out string? error)
    {
        var trimmed = value.Trim();
        var requiresJson = trimmed.StartsWith('{') ||
                           trimmed.StartsWith('[') ||
                           trimmed.StartsWith('"');
        var isJsonScalar = string.Equals(trimmed, "true", StringComparison.Ordinal) ||
                           string.Equals(trimmed, "false", StringComparison.Ordinal) ||
                           string.Equals(trimmed, "null", StringComparison.Ordinal) ||
                           decimal.TryParse(
                               trimmed,
                               NumberStyles.Float,
                               CultureInfo.InvariantCulture,
                               out _);

        if (!requiresJson && !isJsonScalar)
        {
            node = JsonValue.Create(value);
            error = null;
            return true;
        }

        try
        {
            node = JsonNode.Parse(trimmed);
            error = null;
            return true;
        }
        catch (JsonException exception)
        {
            node = null;
            error = $"覆盖值必须是合法 JSON：{exception.Message}";
            return false;
        }
    }

    private static bool TryFindSchema(
        IReadOnlyDictionary<string, JsonObject> schemas,
        string path,
        out JsonObject? schema)
    {
        schema = null;
        var segments = path.Split('.', StringSplitOptions.None);
        if (segments.Any(string.IsNullOrEmpty))
        {
            return false;
        }

        if (segments.Length < 2 ||
            !schemas.TryGetValue(segments[0], out var current))
        {
            return false;
        }

        for (var index = 1; index < segments.Length; index++)
        {
            if (current["properties"] is not JsonObject properties ||
                properties[segments[index]] is not JsonObject property)
            {
                return false;
            }

            current = property;
        }

        schema = current;
        return true;
    }

    private static void ValidateRoot(
        JsonObject root,
        IReadOnlyDictionary<string, JsonObject> schemas,
        bool strict,
        List<OpenCoWorkDiagnostic> diagnostics)
    {
        foreach (var pair in root)
        {
            if (!schemas.TryGetValue(pair.Key, out var schema))
            {
                diagnostics.Add(Unknown(pair.Key, strict));
                continue;
            }

            ValidateNode(pair.Value, schema, pair.Key, strict, diagnostics);
        }

        foreach (var section in schemas.Keys.Except(root.Select(pair => pair.Key), StringComparer.Ordinal))
        {
            diagnostics.Add(Error(
                "OCWCFG007",
                $"缺少必需配置节 '{section}'。",
                section));
        }
    }

    private static void ValidateNode(
        JsonNode? value,
        JsonObject schema,
        string path,
        bool strict,
        List<OpenCoWorkDiagnostic> diagnostics)
    {
        if (value is null)
        {
            diagnostics.Add(Error("OCWCFG007", $"配置值 '{path}' 不能为 null。", path));
            return;
        }

        var expectedType = schema["type"]?.GetValue<string>();
        if (!MatchesType(value, expectedType))
        {
            diagnostics.Add(Error(
                "OCWCFG007",
                $"配置值 '{path}' 的类型必须是 {expectedType ?? "受支持类型"}。",
                path));
            return;
        }

        if (value is JsonObject valueObject)
        {
            ValidateObject(valueObject, schema, path, strict, diagnostics);
        }
        else if (value is JsonArray valueArray &&
                 schema["items"] is JsonObject itemSchema)
        {
            for (var index = 0; index < valueArray.Count; index++)
            {
                ValidateNode(
                    valueArray[index],
                    itemSchema,
                    $"{path}[{index}]",
                    strict,
                    diagnostics);
            }
        }
        else if (value.GetValueKind() == JsonValueKind.String)
        {
            ValidateString(value.GetValue<string>(), schema, path, diagnostics);
        }
        else if (value.GetValueKind() == JsonValueKind.Number)
        {
            ValidateNumber(value, schema, path, diagnostics);
        }
    }

    private static void ValidateObject(
        JsonObject value,
        JsonObject schema,
        string path,
        bool strict,
        List<OpenCoWorkDiagnostic> diagnostics)
    {
        var properties = schema["properties"] as JsonObject;
        var required = schema["required"] as JsonArray;

        if (required is not null)
        {
            foreach (var item in required)
            {
                var name = item?.GetValue<string>();
                if (name is not null && !value.ContainsKey(name))
                {
                    diagnostics.Add(Error(
                        "OCWCFG007",
                        $"缺少必需配置值 '{JoinPath(path, name)}'。",
                        JoinPath(path, name)));
                }
            }
        }

        foreach (var pair in value)
        {
            var childPath = JoinPath(path, pair.Key);
            if (properties?[pair.Key] is JsonObject childSchema)
            {
                ValidateNode(pair.Value, childSchema, childPath, strict, diagnostics);
                continue;
            }

            if (schema["additionalProperties"] is JsonObject additionalSchema)
            {
                ValidateNode(pair.Value, additionalSchema, childPath, strict, diagnostics);
            }
            else if (schema["additionalProperties"]?.GetValue<bool>() == false)
            {
                diagnostics.Add(Unknown(childPath, strict));
            }
        }
    }

    private static void ValidateString(
        string value,
        JsonObject schema,
        string path,
        List<OpenCoWorkDiagnostic> diagnostics)
    {
        if (schema["pattern"] is JsonValue pattern &&
            !Regex.IsMatch(value, pattern.GetValue<string>(), RegexOptions.CultureInvariant))
        {
            diagnostics.Add(Error(
                "OCWCFG007",
                $"配置值 '{path}' 不符合允许格式。",
                path));
        }

        if (schema["enum"] is JsonArray allowed &&
            !allowed.Any(item => string.Equals(item?.GetValue<string>(), value, StringComparison.Ordinal)))
        {
            diagnostics.Add(Error(
                "OCWCFG007",
                $"配置值 '{path}' 不在允许枚举中。",
                path));
        }

        if (string.Equals(
                schema["format"]?.GetValue<string>(),
                "duration",
                StringComparison.Ordinal) &&
            !DurationJsonConverter.TryParse(value, out _))
        {
            diagnostics.Add(Error(
                "OCWCFG007",
                $"配置值 '{path}' 必须是带 ms、s、m 或 h 单位的持续时间。",
                path));
        }
    }

    private static void ValidateNumber(
        JsonNode value,
        JsonObject schema,
        string path,
        List<OpenCoWorkDiagnostic> diagnostics)
    {
        if (!decimal.TryParse(
                value.ToJsonString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var number))
        {
            diagnostics.Add(Error(
                "OCWCFG007",
                $"配置值 '{path}' 超出支持的数字范围。",
                path));
            return;
        }

        if (schema["minimum"] is JsonValue minimum &&
            number < minimum.GetValue<decimal>())
        {
            diagnostics.Add(Error("OCWCFG007", $"配置值 '{path}' 小于允许下限。", path));
        }

        if (schema["maximum"] is JsonValue maximum &&
            number > maximum.GetValue<decimal>())
        {
            diagnostics.Add(Error("OCWCFG007", $"配置值 '{path}' 大于允许上限。", path));
        }
    }

    private static bool MatchesType(JsonNode value, string? expectedType)
    {
        return expectedType switch
        {
            null => true,
            "object" => value is JsonObject,
            "array" => value is JsonArray,
            "string" => value.GetValueKind() == JsonValueKind.String,
            "boolean" => value.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
            "number" => value.GetValueKind() == JsonValueKind.Number,
            "integer" => value.GetValueKind() == JsonValueKind.Number &&
                         decimal.TryParse(
                             value.ToJsonString(),
                             NumberStyles.Float,
                             CultureInfo.InvariantCulture,
                             out var number) &&
                         decimal.Truncate(number) == number,
            _ => true,
        };
    }

    private static Dictionary<Type, JsonElement> BindSections(
        JsonObject root,
        IReadOnlyList<ConfigSectionDescriptor> descriptors,
        List<OpenCoWorkDiagnostic> diagnostics)
    {
        var sections = new Dictionary<Type, JsonElement>();

        foreach (var descriptor in descriptors)
        {
            try
            {
                var node = root[descriptor.Name]
                    ?? throw new JsonException("Section value is null.");
                var instance = node.Deserialize(descriptor.SectionType, SerializerOptions)
                    ?? throw new JsonException("Section binding returned null.");
                var validation = new List<ValidationResult>();

                if (!Validator.TryValidateObject(
                        instance,
                        new ValidationContext(instance),
                        validation,
                        validateAllProperties: true))
                {
                    foreach (var failure in validation)
                    {
                        diagnostics.Add(Error(
                            "OCWCFG008",
                            failure.ErrorMessage ?? $"配置节 '{descriptor.Name}' 验证失败。",
                            descriptor.Name));
                    }

                    continue;
                }

                sections.Add(
                    descriptor.SectionType,
                    JsonSerializer.SerializeToElement(
                        instance,
                        descriptor.SectionType,
                        SerializerOptions));
            }
            catch (Exception exception) when (
                exception is JsonException or NotSupportedException or InvalidOperationException)
            {
                diagnostics.Add(Error(
                    "OCWCFG008",
                    $"配置节 '{descriptor.Name}' 无法绑定：{exception.Message}",
                    descriptor.Name));
            }
        }

        return sections;
    }

    private static void CollectSecrets(
        JsonNode? value,
        JsonObject schema,
        HashSet<string> secrets)
    {
        if (value is null)
        {
            return;
        }

        if (schema["x-opencowork-secret"]?.GetValue<bool>() == true)
        {
            if (value.GetValueKind() == JsonValueKind.String)
            {
                secrets.Add(value.GetValue<string>());
            }

            return;
        }

        if (value is JsonObject valueObject &&
            schema["properties"] is JsonObject properties)
        {
            foreach (var pair in valueObject)
            {
                if (properties[pair.Key] is JsonObject childSchema)
                {
                    CollectSecrets(pair.Value, childSchema, secrets);
                }
            }
        }
        else if (value is JsonArray valueArray &&
                 schema["items"] is JsonObject itemSchema)
        {
            foreach (var item in valueArray)
            {
                CollectSecrets(item, itemSchema, secrets);
            }
        }
    }

    private static void MarkLeaves(
        JsonNode? value,
        string path,
        ConfigValueSource source,
        Dictionary<string, ConfigValueSource> sources)
    {
        if (value is JsonObject valueObject && valueObject.Count > 0)
        {
            foreach (var pair in valueObject)
            {
                MarkLeaves(pair.Value, JoinPath(path, pair.Key), source, sources);
            }

            return;
        }

        sources[path] = source;
    }

    private static string JoinPath(string parent, string child) =>
        string.IsNullOrEmpty(parent) ? child : $"{parent}.{child}";

    private static OpenCoWorkDiagnostic Unknown(string path, bool strict) =>
        new(
            "OCWCFG004",
            strict
                ? OpenCoWorkDiagnosticSeverity.Error
                : OpenCoWorkDiagnosticSeverity.Warning,
            $"未知配置字段 '{path}'。",
            path);

    private static OpenCoWorkDiagnostic Error(string code, string message, string? path) =>
        new(code, OpenCoWorkDiagnosticSeverity.Error, message, path);

    private sealed class DurationJsonConverter : JsonConverter<TimeSpan>
    {
        private static readonly Regex Pattern = new(
            @"^(?<value>0|[0-9]+(?:\.[0-9]+)?)(?<unit>ms|s|m|h)$",
            RegexOptions.CultureInvariant);

        public override TimeSpan Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String ||
                !TryParse(reader.GetString(), out var value))
            {
                throw new JsonException(
                    "Duration must be a string with an ms, s, m, or h suffix.");
            }

            return value;
        }

        public override void Write(
            Utf8JsonWriter writer,
            TimeSpan value,
            JsonSerializerOptions options)
        {
            var milliseconds = value.TotalMilliseconds;
            if (milliseconds % 3_600_000 == 0)
            {
                writer.WriteStringValue($"{milliseconds / 3_600_000:0.################}h");
            }
            else if (milliseconds % 60_000 == 0)
            {
                writer.WriteStringValue($"{milliseconds / 60_000:0.################}m");
            }
            else if (milliseconds % 1_000 == 0)
            {
                writer.WriteStringValue($"{milliseconds / 1_000:0.################}s");
            }
            else
            {
                writer.WriteStringValue($"{milliseconds:0.################}ms");
            }
        }

        internal static bool TryParse(string? text, out TimeSpan value)
        {
            var match = Pattern.Match(text ?? string.Empty);
            if (!match.Success ||
                !decimal.TryParse(
                    match.Groups["value"].Value,
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out var number))
            {
                value = default;
                return false;
            }

            var milliseconds = match.Groups["unit"].Value switch
            {
                "ms" => number,
                "s" => number * 1_000,
                "m" => number * 60_000,
                "h" => number * 3_600_000,
                _ => throw new UnreachableException(),
            };

            try
            {
                value = TimeSpan.FromMilliseconds((double)milliseconds);
                return true;
            }
            catch (OverflowException)
            {
                value = default;
                return false;
            }
        }
    }
}
