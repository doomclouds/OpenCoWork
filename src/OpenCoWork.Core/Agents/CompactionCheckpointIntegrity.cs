using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Core.Agents;

internal static class CompactionCheckpointIntegrity
{
    private static readonly string[] Headings =
    [
        "## 目标与上下文",
        "## 已确认的决策与约束",
        "## 已完成结果",
        "## 关键标识、路径与错误",
        "## 待办与下一步",
    ];

    public static string Sha256(string value) =>
        Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    public static bool IsLowerSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static string SourceMessagesSha256(
        IEnumerable<SessionItemSnapshot> modelHistory,
        long sourceStartSequence,
        long sourceEndSequence,
        int schemaVersion = 1)
    {
        var source = modelHistory
            .Where(item =>
                item.Status == SessionItemStatus.Completed &&
                item.Sequence >= sourceStartSequence &&
                item.Sequence <= sourceEndSequence)
            .OrderBy(item => item.Sequence)
            .ThenBy(item => item.ItemId)
            .ToArray();
        return schemaVersion switch
        {
            1 => LegacySourceMessagesSha256(source),
            2 => ToolAwareSourceMessagesSha256(source),
            _ => throw new InvalidDataException(
                "Compaction checkpoint schema is unsupported."),
        };
    }

    public static bool SourceRangeIsClosed(
        IEnumerable<SessionItemSnapshot> modelHistory,
        long sourceStartSequence,
        long sourceEndSequence)
    {
        var history = modelHistory.ToArray();
        var includedAgentIds = history
            .Where(item =>
                item.Type == SessionItemType.AgentMessage &&
                item.Sequence >= sourceStartSequence &&
                item.Sequence <= sourceEndSequence)
            .Select(item => item.ItemId)
            .ToHashSet();
        return !history.Any(item =>
            item.Type == SessionItemType.ToolCall &&
            (item.Sequence < sourceStartSequence ||
             item.Sequence > sourceEndSequence) &&
            item.Content is ToolCallItemContent
            {
                AgentMessageItemId: { } agentMessageItemId,
            } &&
            includedAgentIds.Contains(agentMessageItemId));
    }

    private static string LegacySourceMessagesSha256(
        IReadOnlyList<SessionItemSnapshot> source)
    {
        if (source.Any(item => item.Type is
                SessionItemType.ToolCall or
                SessionItemType.ToolResult))
        {
            throw new InvalidDataException(
                "Compaction v1 source cannot contain tool messages.");
        }

        var canonical = new StringBuilder();
        foreach (var item in source.Where(item => item.Type is
                     SessionItemType.UserMessage or
                     SessionItemType.AgentMessage))
        {
            if (item.Content is not TextItemContent text)
            {
                throw new InvalidDataException(
                    "Compaction source contains invalid message content.");
            }

            var content = text.Text
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
            canonical.Append(
                item.Type == SessionItemType.UserMessage ? "user:" : "assistant:");
            canonical.Append(Encoding.UTF8.GetByteCount(content));
            canonical.Append(':');
            canonical.Append(content);
            canonical.Append('\n');
        }

        return Sha256(canonical.ToString());
    }

    private static string ToolAwareSourceMessagesSha256(
        IReadOnlyList<SessionItemSnapshot> source)
    {
        IReadOnlyList<JsonElement> input;
        try
        {
            input = ProviderResponsesHistory.Build(source);
        }
        catch (AgentPreparationException exception)
        {
            throw new InvalidDataException(
                "Compaction source contains an incomplete tool message group.",
                exception);
        }

        var canonical = new StringBuilder();
        for (var index = 0; index < input.Count; index++)
        {
            var item = input[index];
            var type = item.GetProperty("type").GetString();
            if (type == "message")
            {
                Append(canonical, item.GetProperty("role").GetString()!);
                Append(canonical, NormalizeLf(item.GetProperty("content").GetString()!));
                Append(canonical, string.Empty);
                while (index + 1 < input.Count &&
                       input[index + 1].GetProperty("type").GetString() ==
                       "function_call")
                {
                    var call = input[++index];
                    Append(canonical, call.GetProperty("call_id").GetString()!);
                    Append(canonical, call.GetProperty("name").GetString()!);
                    Append(canonical, call.GetProperty("arguments").GetString()!);
                }
            }
            else if (type == "function_call")
            {
                Append(canonical, "assistant");
                Append(canonical, string.Empty);
                Append(canonical, string.Empty);
                do
                {
                    var call = input[index];
                    Append(canonical, call.GetProperty("call_id").GetString()!);
                    Append(canonical, call.GetProperty("name").GetString()!);
                    Append(canonical, call.GetProperty("arguments").GetString()!);
                    index++;
                }
                while (index < input.Count &&
                       input[index].GetProperty("type").GetString() == "function_call");
                index--;
            }
            else if (type == "function_call_output")
            {
                Append(canonical, "tool");
                Append(canonical, NormalizeLf(item.GetProperty("output").GetString()!));
                Append(canonical, item.GetProperty("call_id").GetString()!);
            }

            canonical.Append('\n');
        }

        return Sha256(canonical.ToString());
    }

    private static void Append(StringBuilder target, string value)
    {
        target.Append(Encoding.UTF8.GetByteCount(value));
        target.Append(':');
        target.Append(value);
        target.Append('|');
    }

    private static string NormalizeLf(string value) =>
        value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

    public static bool IsValidSummary(string summary)
    {
        var lines = summary.Split('\n');
        var positions = Headings
            .Select(heading => Array.FindIndex(
                lines,
                line => string.Equals(line, heading, StringComparison.Ordinal)))
            .ToArray();
        if (positions[0] != 0 ||
            positions.Any(position => position < 0) ||
            !positions.SequenceEqual(positions.Order()) ||
            lines.Count(line => line.StartsWith("## ", StringComparison.Ordinal)) !=
            Headings.Length)
        {
            return false;
        }

        for (var index = 0; index < positions.Length; index++)
        {
            var end = index + 1 < positions.Length
                ? positions[index + 1]
                : lines.Length;
            if (!lines
                    .Skip(positions[index] + 1)
                    .Take(end - positions[index] - 1)
                    .Any(line => !string.IsNullOrWhiteSpace(line)))
            {
                return false;
            }
        }

        return true;
    }
}
