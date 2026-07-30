using System.Text.Json;

namespace OpenCoWork.Abstractions;

public interface IJsonSchemaValidationService
{
    bool IsValidSchema(JsonElement schema);

    bool Evaluate(JsonElement schema, JsonElement value);
}
