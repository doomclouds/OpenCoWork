using System.Runtime.ExceptionServices;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Core.Capabilities;

public sealed record PluginInstallResult(
    string Id,
    string Version,
    string Sha256,
    CapabilityStatus Status);

public sealed class PluginManager
{
    private readonly CapabilityFileStore _files;
    private readonly SemaphoreSlim _mutationLock = new(1, 1);
    private readonly WorkspaceCapabilityRuntime _runtime;
    private readonly PluginPackageStore _store;

    internal PluginManager(
        PluginPackageStore store,
        CapabilityFileStore files,
        WorkspaceCapabilityRuntime runtime)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public Task<PluginInstallResult> InstallLocalAsync(
        string archivePath,
        CancellationToken cancellationToken = default) =>
        InstallAsync(
            token => _store.StoreLocalAsync(archivePath, token),
            cancellationToken);

    public Task<PluginInstallResult> InstallHttpsAsync(
        Uri artifactUri,
        string artifactSha256,
        CancellationToken cancellationToken = default) =>
        InstallAsync(
            token => _store.StoreHttpsAsync(artifactUri, artifactSha256, token),
            cancellationToken);

    public async Task RemoveAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        await _mutationLock.WaitAsync(cancellationToken);
        try
        {
            var previous = await _files.LoadPluginLockAsync(cancellationToken);
            var plugins = previous.Plugins
                .Where(plugin => !string.Equals(
                    plugin.Id,
                    pluginId,
                    StringComparison.Ordinal))
                .ToArray();
            if (plugins.Length == previous.Plugins.Count)
            {
                return;
            }

            await ApplyAsync(
                previous,
                new PluginLockDocument(1, Array.AsReadOnly(plugins)),
                validate: null,
                cancellationToken);
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    private async Task<PluginInstallResult> InstallAsync(
        Func<CancellationToken, Task<StoredPluginPackage>> store,
        CancellationToken cancellationToken)
    {
        await _mutationLock.WaitAsync(cancellationToken);
        try
        {
            var package = await store(cancellationToken);
            var previous = await _files.LoadPluginLockAsync(cancellationToken);
            var plugins = previous.Plugins
                .Where(plugin => !string.Equals(
                    plugin.Id,
                    package.Manifest.Id,
                    StringComparison.Ordinal))
                .Append(new PluginLockEntry(
                    package.Manifest.Id,
                    package.Manifest.Version,
                    package.ContentSha256,
                    Enabled: true))
                .OrderBy(plugin => plugin.Id, StringComparer.Ordinal)
                .ToArray();
            CapabilityCatalogItem? installed = null;
            await ApplyAsync(
                previous,
                new PluginLockDocument(1, Array.AsReadOnly(plugins)),
                catalog =>
                {
                    installed = catalog.Items.SingleOrDefault(item =>
                        item.Kind == CapabilityKind.Plugin &&
                        string.Equals(
                            item.Id,
                            package.Manifest.Id,
                            StringComparison.Ordinal));
                    return installed is not null &&
                           string.Equals(
                               installed.Source.Sha256,
                               package.ContentSha256,
                               StringComparison.Ordinal) &&
                           installed.Status is
                               CapabilityStatus.Ready or
                               CapabilityStatus.PendingTrust or
                               CapabilityStatus.Disabled;
                },
                cancellationToken);
            return new PluginInstallResult(
                package.Manifest.Id,
                package.Manifest.Version,
                package.ContentSha256,
                installed!.Status);
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    private async Task ApplyAsync(
        PluginLockDocument previous,
        PluginLockDocument next,
        Func<CapabilityCatalog, bool>? validate,
        CancellationToken cancellationToken)
    {
        await _files.SavePluginLockAsync(next, cancellationToken);
        try
        {
            var catalog = await _runtime.RefreshDiscoveredAsync(cancellationToken);
            if (validate is not null && !validate(catalog))
            {
                throw new PluginPackageException(
                    PluginErrorCodes.LoadFailed,
                    "Plugin installation failed.");
            }
        }
        catch (Exception error)
        {
            try
            {
                await _files.SavePluginLockAsync(previous, CancellationToken.None);
                await _runtime.RefreshDiscoveredAsync(CancellationToken.None);
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException(
                    "Plugin mutation and rollback failed.",
                    error,
                    rollbackError);
            }

            ExceptionDispatchInfo.Capture(error).Throw();
            throw;
        }
    }
}
