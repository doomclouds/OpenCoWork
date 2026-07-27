using System.Security.Cryptography;
using System.Text;
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
        long sourceEndSequence)
    {
        var canonical = new StringBuilder();
        foreach (var item in modelHistory
                     .Where(item =>
                         item.Status == SessionItemStatus.Completed &&
                         item.Sequence >= sourceStartSequence &&
                         item.Sequence <= sourceEndSequence &&
                         item.Type is SessionItemType.UserMessage or
                             SessionItemType.AgentMessage)
                     .OrderBy(item => item.Sequence)
                     .ThenBy(item => item.ItemId))
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
