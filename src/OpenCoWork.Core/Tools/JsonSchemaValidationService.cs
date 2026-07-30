using System.Security.Cryptography;
using System.Text.Json;
using Json.Schema;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Core.Tools;

internal sealed class JsonSchemaValidationService : IJsonSchemaValidationService
{
    public bool IsValidSchema(JsonElement schema) =>
        TryBuild(schema, out _);

    public bool Evaluate(JsonElement schema, JsonElement value) =>
        TryBuild(schema, out var compiled) &&
        compiled!.Evaluate(
            value,
            new EvaluationOptions
            {
                OutputFormat = OutputFormat.Flag,
                RequireFormatValidation = false,
            }).IsValid;

    private static bool TryBuild(JsonElement value, out JsonSchema? schema)
    {
        schema = null;
        if (value.ValueKind != JsonValueKind.Object || !UsesClosedReferences(value))
        {
            return false;
        }

        try
        {
            var schemaBytes = JsonSerializer.SerializeToUtf8Bytes(value);
            var registry = new SchemaRegistry();
            registry.Fetch = static (_, _) => throw new JsonSchemaException(
                "External schema resolution is disabled.");
            schema = JsonSchema.Build(
                value,
                new BuildOptions
                {
                    Dialect = Dialect.Draft202012,
                    SchemaRegistry = registry,
                },
                new Uri(
                    "urn:opencowork:automation-input-schema:" +
                    Convert.ToHexString(SHA256.HashData(schemaBytes)).ToLowerInvariant()));
            return true;
        }
        catch (Exception exception) when (
            exception is JsonException or JsonSchemaException or
                ArgumentException or InvalidOperationException or FormatException)
        {
            schema = null;
            return false;
        }
    }

    private static bool UsesClosedReferences(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray().All(UsesClosedReferences);
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        foreach (var property in value.EnumerateObject())
        {
            if (property.Name is "$ref" or "$dynamicRef" &&
                (property.Value.ValueKind != JsonValueKind.String ||
                 !property.Value.GetString()!.StartsWith('#')))
            {
                return false;
            }

            if (property.Name == "$schema" &&
                (property.Value.ValueKind != JsonValueKind.String ||
                 property.Value.GetString() !=
                 "https://json-schema.org/draft/2020-12/schema"))
            {
                return false;
            }

            if (!UsesClosedReferences(property.Value))
            {
                return false;
            }
        }

        return true;
    }
}
