using System.Security.Cryptography;
using System.Text;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Capabilities;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class SkillCatalogTests
{
    [Fact]
    public async Task Trusted_workspace_skills_apply_variant_overrides_deterministically()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (workspace, user) = CreateDirectories();
        try
        {
            var baseText = Skill(
                "acme/review",
                "Code Review",
                "Review changes.",
                "Review correctness first.");
            var variantText = Skill(
                "acme/review-strict",
                "Strict Review",
                "Review with strict checks.",
                "Reject every correctness gap.",
                "acme/review");
            WriteSkill(workspace, "review", baseText);
            WriteSkill(workspace, "review-strict", variantText);

            var paths = new CapabilityPersistencePaths(
                new OpenCoWorkPaths(workspace),
                user);
            var store = new CapabilityFileStore(paths);
            await store.SaveTrustDecisionsAsync(
                new TrustDecisionsDocument(
                    1,
                    [
                        Trust(
                            workspace,
                            "acme/review",
                            Hash(Normalize(baseText))),
                        Trust(
                            workspace,
                            "acme/review-strict",
                            Hash(Normalize(variantText))),
                    ]),
                cancellationToken);
            await store.SaveWorkspaceOverridesAsync(
                new CapabilityOverridesDocument(
                    1,
                    [],
                    [new SkillVariantOverride("acme/review", "acme/review-strict")]),
                cancellationToken);

            var result = await new SkillCatalog(paths, store)
                .DiscoverAsync(cancellationToken: cancellationToken);

            Assert.Equal(
                ["acme/review", "acme/review-strict"],
                result.Snapshot.Items.Select(item => item.Id));
            var baseSkill = result.Snapshot.Items[0];
            var variant = result.Snapshot.Items[1];
            Assert.False(baseSkill.IsActive);
            Assert.Equal("acme/review-strict", baseSkill.SelectedVariantId);
            Assert.True(variant.IsActive);
            Assert.Null(variant.SelectedVariantId);
            Assert.Equal(
                Hash("Reject every correctness gap."),
                variant.ContentSha256);
            Assert.All(
                result.Contributions,
                set => Assert.Equal(CapabilityStatus.Ready, Assert.Single(set.Items).Status));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
            Directory.Delete(user, recursive: true);
        }
    }

    [Fact]
    public async Task User_disable_is_a_floor_and_invalid_skill_is_isolated()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (workspace, user) = CreateDirectories();
        try
        {
            var valid = Skill(
                "acme/review",
                "Code Review",
                "Review changes.",
                "Review correctness first.");
            WriteSkill(workspace, "review", valid);
            WriteSkill(
                workspace,
                "broken",
                """
                ---
                id: acme/broken
                id: acme/duplicate
                name: Broken
                description: Broken.
                ---
                body
                """);
            var paths = new CapabilityPersistencePaths(
                new OpenCoWorkPaths(workspace),
                user);
            var store = new CapabilityFileStore(paths);
            await store.SaveTrustDecisionsAsync(
                new TrustDecisionsDocument(
                    1,
                    [Trust(workspace, "acme/review", Hash(Normalize(valid)))]),
                cancellationToken);
            await store.SaveUserOverridesAsync(
                new CapabilityOverridesDocument(
                    1,
                    [new DisabledCapability(CapabilityKind.Skill, "acme/review")],
                    []),
                cancellationToken);

            var result = await new SkillCatalog(paths, store)
                .DiscoverAsync(cancellationToken: cancellationToken);

            Assert.Empty(result.Snapshot.Items);
            Assert.Contains(
                result.Contributions.SelectMany(set => set.Items),
                item => item.Id == "acme/review" &&
                        item.Status == CapabilityStatus.Disabled);
            Assert.Contains(
                result.Contributions.SelectMany(set => set.Items),
                item => item.Status == CapabilityStatus.Faulted &&
                        item.DiagnosticCodes.Contains(SkillErrorCodes.DefinitionInvalid));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
            Directory.Delete(user, recursive: true);
        }
    }

    [Fact]
    public async Task Oversized_skill_is_not_truncated_or_snapshotted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (workspace, user) = CreateDirectories();
        try
        {
            WriteSkill(
                workspace,
                "large",
                Skill(
                    "acme/large",
                    "Large",
                    "Too large.",
                    new string('x', 64 * 1024 + 1)));
            var paths = new CapabilityPersistencePaths(
                new OpenCoWorkPaths(workspace),
                user);

            var result = await new SkillCatalog(
                    paths,
                    new CapabilityFileStore(paths))
                .DiscoverAsync(cancellationToken: cancellationToken);

            Assert.Empty(result.Snapshot.Items);
            Assert.Contains(
                result.Contributions.SelectMany(set => set.Items),
                item => item.DiagnosticCodes.Contains(SkillErrorCodes.TooLarge));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
            Directory.Delete(user, recursive: true);
        }
    }

    [Fact]
    public async Task Published_catalog_lease_carries_the_matching_skill_snapshot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (workspace, user) = CreateDirectories();
        try
        {
            var text = Skill(
                "acme/review",
                "Code Review",
                "Review changes.",
                "Review correctness first.");
            WriteSkill(workspace, "review", text);
            var workspacePaths = new OpenCoWorkPaths(workspace);
            var paths = new CapabilityPersistencePaths(workspacePaths, user);
            var store = new CapabilityFileStore(paths);
            await store.SaveTrustDecisionsAsync(
                new TrustDecisionsDocument(
                    1,
                    [Trust(workspace, "acme/review", Hash(Normalize(text)))]),
                cancellationToken);
            var providers = new ProviderDeclarationCatalog(workspacePaths, _ => null);
            var runtime = new WorkspaceCapabilityRuntime(
                [],
                new WorkspaceCapabilityDiscovery(
                    new SkillCatalog(paths, store),
                    providers));

            await runtime.StartAsync(cancellationToken);
            using var lease = runtime.AcquireSnapshot();

            Assert.Equal(lease.Catalog.Revision, runtime.CurrentCatalog.Revision);
            Assert.Equal("acme/review", Assert.Single(lease.Skills.Items).Id);
            await runtime.StopAsync(cancellationToken);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
            Directory.Delete(user, recursive: true);
        }
    }

    private static CapabilityTrustDecision Trust(
        string workspace,
        string id,
        string sha256) =>
        new(
            workspace,
            CapabilitySourceKind.Workspace,
            id,
            SourceVersion: null,
            sha256,
            [CapabilityTrustScope.PromptContribution],
            []);

    private static string Skill(
        string id,
        string name,
        string description,
        string body,
        string? variantOf = null) =>
        $"""
         ---
         id: {id}
         name: {name}
         description: {description}
         {(variantOf is null ? string.Empty : $"variantOf: {variantOf}\n")}---
         {body}
         """;

    private static void WriteSkill(string workspace, string folder, string content)
    {
        var directory = Path.Combine(workspace, ".opencowork", "skills", folder);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "SKILL.md"), content);
    }

    private static (string Workspace, string User) CreateDirectories()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-skills-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        var user = Path.Combine(root, "user");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(user);
        return (workspace, user);
    }

    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}
