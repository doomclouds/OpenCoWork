using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Core.Capabilities;

internal static class SkillErrorCodes
{
    public const string DefinitionInvalid = "skill.definitionInvalid";
    public const string TooLarge = "skill.tooLarge";
    public const string TrustRequired = "trust.required";
    public const string VariantInvalid = "skill.variantInvalid";
    public const string VariantUnavailable = "skill.variantUnavailable";
}

internal sealed record SkillDiscoveryResult(
    IReadOnlyList<CapabilityContributionSet> Contributions,
    EffectiveSkillSnapshot Snapshot);

internal sealed record WorkspaceCapabilityDiscoveryResult(
    IReadOnlyList<CapabilityContributionSet> Contributions,
    EffectiveSkillSnapshot Skills);

internal sealed class WorkspaceCapabilityDiscovery(
    SkillCatalog skills,
    ProviderDeclarationCatalog providers,
    PluginRuntime? plugins = null)
{
    private readonly SkillCatalog _skills =
        skills ?? throw new ArgumentNullException(nameof(skills));
    private readonly ProviderDeclarationCatalog _providers =
        providers ?? throw new ArgumentNullException(nameof(providers));
    private readonly PluginRuntime? _plugins = plugins;

    public async Task<WorkspaceCapabilityDiscoveryResult> DiscoverAsync(
        CancellationToken cancellationToken)
    {
        var skillResult = await _skills.DiscoverAsync(
            cancellationToken: cancellationToken);
        var pluginResult = _plugins is null
            ? new PluginDiscoveryResult([])
            : await _plugins.DiscoverAsync(cancellationToken);
        return new WorkspaceCapabilityDiscoveryResult(
            Array.AsReadOnly(
                skillResult.Contributions
                    .Concat(_providers.Contributions)
                    .Concat(pluginResult.Contributions)
                    .ToArray()),
            skillResult.Snapshot);
    }

    public IDisposable? AcquirePluginSnapshot(EffectiveToolSnapshot snapshot) =>
        _plugins?.AcquireSnapshotLease(snapshot);

    public Task StopAsync(CancellationToken cancellationToken) =>
        _plugins?.StopAsync(cancellationToken) ?? Task.CompletedTask;
}

internal sealed partial class SkillCatalog(
    CapabilityPersistencePaths paths,
    CapabilityFileStore store)
{
    private const int MaximumBodyBytes = 64 * 1024;
    private const int MaximumFileBytes = MaximumBodyBytes + 8 * 1024;
    private const int MaximumSnapshotBytes = 1024 * 1024;
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly CapabilityPersistencePaths _paths =
        paths ?? throw new ArgumentNullException(nameof(paths));
    private readonly CapabilityFileStore _store =
        store ?? throw new ArgumentNullException(nameof(store));

    public async Task<SkillDiscoveryResult> DiscoverAsync(
        IReadOnlyList<SkillVariantOverride>? threadVariants = null,
        CancellationToken cancellationToken = default)
    {
        var trust = await _store.LoadTrustDecisionsAsync(cancellationToken);
        var userOverrides = await _store.LoadUserOverridesAsync(cancellationToken);
        var workspaceOverrides =
            await _store.LoadWorkspaceOverridesAsync(cancellationToken);
        var discovered = new List<DiscoveredSkill>();
        var invalid = new List<InvalidSkill>();
        DiscoverDirectory(
            _paths.WorkspacePaths.SkillsDirectory,
            CapabilitySourceKind.Workspace,
            _paths.ResolveWorkspaceSkill,
            discovered,
            invalid,
            cancellationToken);
        DiscoverDirectory(
            _paths.UserSkillsDirectory,
            CapabilitySourceKind.User,
            _paths.ResolveUserSkill,
            discovered,
            invalid,
            cancellationToken);

        var duplicateIds = discovered
            .GroupBy(skill => skill.Id, StringComparer.Ordinal)
            .Where(group => group.Skip(1).Any())
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        var unique = discovered
            .Where(skill => !duplicateIds.Contains(skill.Id))
            .ToDictionary(skill => skill.Id, StringComparer.Ordinal);
        var states = discovered.ToDictionary(
            skill => skill,
            skill => StateFor(
                skill,
                duplicateIds,
                unique,
                trust,
                userOverrides,
                workspaceOverrides));

        var selectedVariants = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var skill in unique.Values
                     .Where(skill => skill.VariantOf is null)
                     .OrderBy(skill => skill.Id, StringComparer.Ordinal))
        {
            var selected = SelectVariant(
                skill.Id,
                threadVariants ?? [],
                workspaceOverrides.SkillVariants,
                userOverrides.SkillVariants,
                unique,
                states,
                out var unavailable);
            if (selected is not null)
            {
                selectedVariants.Add(skill.Id, selected);
            }
            else if (unavailable)
            {
                states[skill] = states[skill].WithDiagnostic(
                    SkillErrorCodes.VariantUnavailable);
            }
        }

        var items = new List<EffectiveSkillSnapshotItem>();
        foreach (var skill in discovered.OrderBy(skill => skill.Id, StringComparer.Ordinal))
        {
            var state = states[skill];
            if (state.Status != CapabilityStatus.Ready ||
                duplicateIds.Contains(skill.Id))
            {
                continue;
            }

            var baseId = skill.VariantOf ?? skill.Id;
            if (!unique.TryGetValue(baseId, out var baseSkill) ||
                states[baseSkill].Status != CapabilityStatus.Ready)
            {
                continue;
            }

            var selected = selectedVariants.GetValueOrDefault(baseId);
            var active = skill.VariantOf is null
                ? selected is null
                : string.Equals(selected, skill.Id, StringComparison.Ordinal);
            items.Add(new EffectiveSkillSnapshotItem(
                skill.Id,
                skill.Source,
                skill.Description,
                skill.Body,
                skill.BodySha256,
                active,
                skill.VariantOf is null ? selected : null));
        }

        IsolateSnapshotOverflow(items, states, unique);
        var contributions = discovered
            .OrderBy(skill => skill.Id, StringComparer.Ordinal)
            .ThenBy(skill => skill.Source.Kind)
            .Select(skill => Contribution(skill, states[skill]))
            .Concat(invalid
                .OrderBy(item => item.SafeId, StringComparer.Ordinal)
                .Select(Contribution))
            .ToArray();
        return new SkillDiscoveryResult(
            Array.AsReadOnly(contributions),
            EffectiveSkillSnapshot.Create(items));
    }

    private static SkillState StateFor(
        DiscoveredSkill skill,
        IReadOnlySet<string> duplicateIds,
        IReadOnlyDictionary<string, DiscoveredSkill> unique,
        TrustDecisionsDocument trust,
        CapabilityOverridesDocument userOverrides,
        CapabilityOverridesDocument workspaceOverrides)
    {
        if (duplicateIds.Contains(skill.Id))
        {
            return new SkillState(
                CapabilityStatus.Conflict,
                [CapabilityErrorCodes.Conflict]);
        }

        if (skill.VariantOf is not null &&
            (!unique.TryGetValue(skill.VariantOf, out var baseSkill) ||
             baseSkill.VariantOf is not null))
        {
            return new SkillState(
                CapabilityStatus.Faulted,
                [SkillErrorCodes.VariantInvalid]);
        }

        if (userOverrides.IsDisabled(CapabilityKind.Skill, skill.Id) ||
            workspaceOverrides.IsDisabled(CapabilityKind.Skill, skill.Id))
        {
            return new SkillState(CapabilityStatus.Disabled, []);
        }

        var decision = trust.Decisions.SingleOrDefault(item =>
            item.Matches(
                skill.WorkspacePath,
                skill.Source.Kind,
                skill.Id,
                sourceVersion: null,
                skill.Source.Sha256));
        return decision?.AllowedScopes.Contains(
                   CapabilityTrustScope.PromptContribution) == true
            ? new SkillState(CapabilityStatus.Ready, [])
            : new SkillState(
                CapabilityStatus.PendingTrust,
                [SkillErrorCodes.TrustRequired]);
    }

    private static string? SelectVariant(
        string baseId,
        IReadOnlyList<SkillVariantOverride> thread,
        IReadOnlyList<SkillVariantOverride> workspace,
        IReadOnlyList<SkillVariantOverride> user,
        IReadOnlyDictionary<string, DiscoveredSkill> unique,
        IReadOnlyDictionary<DiscoveredSkill, SkillState> states,
        out bool unavailable)
    {
        unavailable = false;
        foreach (var source in new[] { thread, workspace, user })
        {
            var selected = source.FirstOrDefault(item =>
                string.Equals(item.BaseId, baseId, StringComparison.Ordinal));
            if (selected is null)
            {
                continue;
            }

            if (unique.TryGetValue(selected.VariantId, out var variant) &&
                string.Equals(variant.VariantOf, baseId, StringComparison.Ordinal) &&
                states[variant].Status == CapabilityStatus.Ready)
            {
                return variant.Id;
            }

            unavailable = true;
        }

        return null;
    }

    private static void IsolateSnapshotOverflow(
        List<EffectiveSkillSnapshotItem> items,
        Dictionary<DiscoveredSkill, SkillState> states,
        IReadOnlyDictionary<string, DiscoveredSkill> unique)
    {
        while (JsonSerializer.SerializeToUtf8Bytes(
                   items.OrderBy(item => item.Id, StringComparer.Ordinal)).Length >
               MaximumSnapshotBytes)
        {
            var removed = items.OrderBy(item => item.Id, StringComparer.Ordinal).Last();
            items.Remove(removed);
            states[unique[removed.Id]] = states[unique[removed.Id]]
                .WithDiagnostic(SkillErrorCodes.TooLarge) with
            {
                Status = CapabilityStatus.Faulted,
            };
        }
    }

    private void DiscoverDirectory(
        string root,
        CapabilitySourceKind kind,
        Func<string, OpenCoWork.Core.Workspaces.ResolvedWorkspacePath> resolve,
        List<DiscoveredSkill> discovered,
        List<InvalidSkill> invalid,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(root)
                     .OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = Path.GetFileName(directory);
            var relative = Path.Combine(folder, "SKILL.md");
            try
            {
                var path = resolve(relative);
                if (!File.Exists(path.PhysicalPath))
                {
                    continue;
                }

                var text = ReadNormalized(path.PhysicalPath);
                var parsed = Parse(text);
                var sourceSha256 = Hash(text);
                discovered.Add(new DiscoveredSkill(
                    parsed.Id,
                    parsed.Name,
                    parsed.Description,
                    parsed.VariantOf,
                    parsed.Body,
                    Hash(parsed.Body),
                    new CapabilitySourceDescriptor(
                        kind,
                        parsed.Id,
                        version: null,
                        sourceSha256),
                    _paths.WorkspacePaths.WorkspaceRoot));
            }
            catch (SkillCatalogException exception)
            {
                invalid.Add(new InvalidSkill(
                    SafeInvalidId(kind, folder),
                    kind,
                    exception.Code));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or
                    DecoderFallbackException or ArgumentException)
            {
                invalid.Add(new InvalidSkill(
                    SafeInvalidId(kind, folder),
                    kind,
                    SkillErrorCodes.DefinitionInvalid));
            }
        }
    }

    private static ParsedSkill Parse(string text)
    {
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
        {
            throw Invalid();
        }

        var end = text.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0)
        {
            throw Invalid();
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text[4..end].Split('\n'))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                throw Invalid();
            }

            var key = line[..separator];
            var value = line[(separator + 1)..].Trim();
            if (value.Length == 0 ||
                !AllowedFields.Contains(key) ||
                !fields.TryAdd(key, value) ||
                value.Any(char.IsControl))
            {
                throw Invalid();
            }
        }

        if (!fields.TryGetValue("id", out var id) ||
            !SkillIdPattern().IsMatch(id) ||
            id.StartsWith("opencowork/", StringComparison.Ordinal) ||
            !fields.TryGetValue("name", out var name) ||
            !fields.TryGetValue("description", out var description))
        {
            throw Invalid();
        }

        var variantOf = fields.GetValueOrDefault("variantOf");
        if (variantOf is not null &&
            (!SkillIdPattern().IsMatch(variantOf) ||
             string.Equals(id, variantOf, StringComparison.Ordinal)))
        {
            throw Invalid();
        }

        var body = text[(end + 5)..];
        if (StrictUtf8.GetByteCount(body) > MaximumBodyBytes)
        {
            throw new SkillCatalogException(SkillErrorCodes.TooLarge);
        }

        return new ParsedSkill(id, name, description, variantOf, body);
    }

    private static string ReadNormalized(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.SequentialScan);
        var bytes = new byte[MaximumFileBytes + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = stream.Read(bytes, length, bytes.Length - length);
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        if (length > MaximumFileBytes || stream.ReadByte() != -1)
        {
            throw new SkillCatalogException(SkillErrorCodes.TooLarge);
        }

        var content = bytes.AsSpan(0, length);
        if (content.StartsWith(Utf8Bom))
        {
            content = content[3..];
        }

        var text = StrictUtf8.GetString(content);
        if (text.Contains('\0', StringComparison.Ordinal))
        {
            throw Invalid();
        }

        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
    }

    private static CapabilityContributionSet Contribution(
        DiscoveredSkill skill,
        SkillState state) =>
        new(
            skill.Source,
            [
                new CapabilityContribution(
                    CapabilityKind.Skill,
                    skill.Id,
                    skill.Name,
                    skill.Description,
                    state.Status,
                    [CapabilityTrustScope.PromptContribution],
                    generation: 1,
                    state.Diagnostics),
            ]);

    private static CapabilityContributionSet Contribution(InvalidSkill skill)
    {
        var digest = Hash(skill.SafeId);
        return new CapabilityContributionSet(
            new CapabilitySourceDescriptor(
                skill.Kind,
                skill.SafeId,
                version: null,
                digest),
            [
                new CapabilityContribution(
                    CapabilityKind.Skill,
                    skill.SafeId,
                    "Invalid Skill",
                    "Skill definition is invalid.",
                    CapabilityStatus.Faulted,
                    [CapabilityTrustScope.PromptContribution],
                    generation: 0,
                    [skill.Code]),
            ]);
    }

    private static string SafeInvalidId(CapabilitySourceKind kind, string folder) =>
        $"invalid/{Hash($"{kind}\0{folder}")[..16]}";

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static SkillCatalogException Invalid() =>
        new(SkillErrorCodes.DefinitionInvalid);

    private static readonly HashSet<string> AllowedFields =
        new(["id", "name", "description", "variantOf"], StringComparer.Ordinal);

    [GeneratedRegex(
        "^[a-z0-9](?:[a-z0-9.-]{0,62})/[a-z0-9](?:[a-z0-9.-]{0,62})$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SkillIdPattern();

    private sealed record ParsedSkill(
        string Id,
        string Name,
        string Description,
        string? VariantOf,
        string Body);

    private sealed record DiscoveredSkill(
        string Id,
        string Name,
        string Description,
        string? VariantOf,
        string Body,
        string BodySha256,
        CapabilitySourceDescriptor Source,
        string WorkspacePath);

    private sealed record InvalidSkill(
        string SafeId,
        CapabilitySourceKind Kind,
        string Code);

    private sealed record SkillState(
        CapabilityStatus Status,
        IReadOnlyList<string> Diagnostics)
    {
        public SkillState WithDiagnostic(string code) =>
            this with
            {
                Diagnostics = Diagnostics
                    .Append(code)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
            };
    }

    private sealed class SkillCatalogException(string code) : Exception
    {
        public string Code { get; } = code;
    }
}
