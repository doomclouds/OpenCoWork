using System.Data.Common;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Core.State;

internal sealed class ProjectWriterLeaseService(
    IWorkspaceStateStore store,
    TimeProvider timeProvider) : IProjectWriterLeaseService
{
    public ValueTask<ProjectWriterLease?> TryAcquireAsync(
        ProjectWriterLeaseOwner owner,
        CancellationToken cancellationToken = default)
    {
        ValidateOwner(owner);
        var now = timeProvider.GetUtcNow();
        return store.WriteAsync(
            async (connection, transaction, token) =>
            {
                var existing = await ReadAsync(connection, transaction, token);
                if (existing is not null &&
                    existing.ExpiresAtUtc > now &&
                    existing.Owner == owner)
                {
                    return existing;
                }

                if (existing is not null && existing.ExpiresAtUtc > now)
                {
                    return null;
                }

                var leaseId = Guid.CreateVersion7(now);
                var expiresAtUtc = now + ProjectWriterLeaseLimits.LeaseDuration;
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    UPDATE project_writer_lease
                    SET owner_kind = $owner_kind,
                        owner_id = $owner_id,
                        lease_id = $lease_id,
                        expires_utc = $expires_utc,
                        updated_utc = $updated_utc
                    WHERE id = 1
                      AND (lease_id IS NULL OR expires_utc <= $updated_utc);
                    """;
                Add(command, "$owner_kind", OwnerKind(owner.Kind));
                Add(command, "$owner_id", owner.OwnerId.ToString("D"));
                Add(command, "$lease_id", leaseId.ToString("D"));
                Add(command, "$expires_utc", expiresAtUtc.ToUnixTimeMilliseconds());
                Add(command, "$updated_utc", now.ToUnixTimeMilliseconds());
                return await command.ExecuteNonQueryAsync(token) == 1
                    ? new ProjectWriterLease(leaseId, owner, expiresAtUtc)
                    : null;
            },
            cancellationToken);
    }

    public ValueTask<ProjectWriterLease?> RenewAsync(
        ProjectWriterLeaseOwner owner,
        Guid leaseId,
        CancellationToken cancellationToken = default)
    {
        ValidateOwner(owner);
        ValidateVersionSeven(leaseId, nameof(leaseId));
        var now = timeProvider.GetUtcNow();
        var expiresAtUtc = now + ProjectWriterLeaseLimits.LeaseDuration;
        return store.WriteAsync(
            async (connection, transaction, token) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    UPDATE project_writer_lease
                    SET expires_utc = $expires_utc,
                        updated_utc = $updated_utc
                    WHERE id = 1
                      AND owner_kind = $owner_kind
                      AND owner_id = $owner_id
                      AND lease_id = $lease_id
                      AND expires_utc > $updated_utc;
                    """;
                Add(command, "$owner_kind", OwnerKind(owner.Kind));
                Add(command, "$owner_id", owner.OwnerId.ToString("D"));
                Add(command, "$lease_id", leaseId.ToString("D"));
                Add(command, "$expires_utc", expiresAtUtc.ToUnixTimeMilliseconds());
                Add(command, "$updated_utc", now.ToUnixTimeMilliseconds());
                return await command.ExecuteNonQueryAsync(token) == 1
                    ? new ProjectWriterLease(leaseId, owner, expiresAtUtc)
                    : null;
            },
            cancellationToken);
    }

    public ValueTask<bool> ReleaseAsync(
        ProjectWriterLeaseOwner owner,
        Guid leaseId,
        CancellationToken cancellationToken = default)
    {
        ValidateOwner(owner);
        ValidateVersionSeven(leaseId, nameof(leaseId));
        var now = timeProvider.GetUtcNow();
        return store.WriteAsync(
            async (connection, transaction, token) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    UPDATE project_writer_lease
                    SET owner_kind = NULL,
                        owner_id = NULL,
                        lease_id = NULL,
                        expires_utc = NULL,
                        updated_utc = $updated_utc
                    WHERE id = 1
                      AND owner_kind = $owner_kind
                      AND owner_id = $owner_id
                      AND lease_id = $lease_id;
                    """;
                Add(command, "$owner_kind", OwnerKind(owner.Kind));
                Add(command, "$owner_id", owner.OwnerId.ToString("D"));
                Add(command, "$lease_id", leaseId.ToString("D"));
                Add(command, "$updated_utc", now.ToUnixTimeMilliseconds());
                return await command.ExecuteNonQueryAsync(token) == 1;
            },
            cancellationToken);
    }

    private static async ValueTask<ProjectWriterLease?> ReadAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT owner_kind, owner_id, lease_id, expires_utc
            FROM project_writer_lease
            WHERE id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0))
        {
            return null;
        }

        return new ProjectWriterLease(
            Guid.Parse(reader.GetString(2)),
            new ProjectWriterLeaseOwner(
                ParseOwnerKind(reader.GetString(0)),
                Guid.Parse(reader.GetString(1))),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)));
    }

    private static void ValidateOwner(ProjectWriterLeaseOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ValidateVersionSeven(owner.OwnerId, nameof(owner));
    }

    private static void ValidateVersionSeven(Guid value, string parameterName)
    {
        if (value.Version != 7)
        {
            throw new ArgumentException("Value must be a UUIDv7.", parameterName);
        }
    }

    private static string OwnerKind(ProjectWriterLeaseOwnerKind value) =>
        value switch
        {
            ProjectWriterLeaseOwnerKind.CoWorkAgentRun => "coWorkAgentRun",
            ProjectWriterLeaseOwnerKind.AutomationRun => "automationRun",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

    private static ProjectWriterLeaseOwnerKind ParseOwnerKind(string value) =>
        value switch
        {
            "coWorkAgentRun" => ProjectWriterLeaseOwnerKind.CoWorkAgentRun,
            "automationRun" => ProjectWriterLeaseOwnerKind.AutomationRun,
            _ => throw new InvalidDataException("Project writer lease owner is invalid."),
        };

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
