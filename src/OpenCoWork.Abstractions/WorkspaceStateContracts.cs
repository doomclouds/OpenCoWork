using System.Data.Common;

namespace OpenCoWork.Abstractions;

public sealed record WorkspaceRuntimeDescriptor(
    string WorkspaceRoot,
    string DataRoot,
    string RuntimeRoot,
    string TeamsRoot,
    string MissionsRoot,
    string SubAgentsRoot,
    string WorktreesRoot);

public sealed record ExecutionWorkspaceDescriptor(
    CoWorkWorkspaceMode Mode,
    string WorkspaceRoot,
    string ScratchpadRoot,
    Guid? WorktreeId,
    string? WorktreeRoot,
    string? BaseCommitSha);

public sealed record CoWorkThreadProvenance(
    Guid AgentRunId,
    CoWorkAgentRunKind RunKind,
    Guid? MissionId = null,
    Guid? MissionTaskId = null,
    Guid? MemberId = null,
    Guid? ParentAgentRunId = null,
    Guid? ParentThreadId = null);

public interface IWorkspaceStateStore
{
    ValueTask<T> ReadAsync<T>(
        Func<DbConnection, CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken = default);

    ValueTask<T> WriteAsync<T>(
        Func<DbConnection, DbTransaction, CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken = default);
}

public interface IWorkspaceStateMigrationContributor
{
    int TargetVersion { get; }

    ValueTask ApplyAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken);

    ValueTask ValidateAsync(
        DbConnection connection,
        CancellationToken cancellationToken);
}

public sealed record ManagedWorktreeCreateRequest(
    Guid AgentRunId,
    string BaseCommitSha);

public sealed record ManagedWorktreeDescriptor(
    Guid WorktreeId,
    string WorktreeRoot,
    string BaseCommitSha,
    CoWorkWorktreeStatus Status,
    bool IsDirty);

public interface IManagedWorktreeService
{
    ValueTask<ManagedWorktreeDescriptor> CreateAsync(
        Guid agentRunId,
        CancellationToken cancellationToken = default);

    ValueTask<ManagedWorktreeDescriptor> CreateAsync(
        ManagedWorktreeCreateRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<ManagedWorktreeDescriptor?> GetAsync(
        Guid worktreeId,
        CancellationToken cancellationToken = default);

    ValueTask<ManagedWorktreeDescriptor> RemoveAsync(
        Guid worktreeId,
        CancellationToken cancellationToken = default);
}

public interface ISensitiveDataService
{
    bool ContainsSensitiveData(string value);

    string Redact(string value);

    ValueTask<bool> ContainsSensitiveDataAsync(
        Stream source,
        CancellationToken cancellationToken = default);
}
