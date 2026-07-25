using OpenCoWork.Abstractions;

namespace OpenCoWork.Core.Hosting;

public sealed class ModuleRegistry
{
    private readonly IReadOnlyDictionary<string, ModuleDescriptor> _modules;

    public ModuleRegistry(IEnumerable<ModuleDescriptor> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        var catalog = modules.ToArray();
        if (catalog.Length == 0)
        {
            throw new ModuleRegistryException("OCWMOD004", "The module catalog is empty.");
        }

        foreach (var module in catalog.OrderBy(module => module.Id, StringComparer.Ordinal))
        {
            if (!IsLowerKebabCase(module.Id))
            {
                throw new ModuleRegistryException(
                    "OCWMOD010",
                    $"Module ID '{module.Id}' must use lower kebab-case.");
            }

            var invalidDependency = module.Dependencies
                .Order(StringComparer.Ordinal)
                .FirstOrDefault(dependency => !IsLowerKebabCase(dependency));
            if (invalidDependency is not null)
            {
                throw new ModuleRegistryException(
                    "OCWMOD010",
                    $"Module dependency ID '{invalidDependency}' must use lower kebab-case.");
            }
        }

        var duplicate = catalog
            .GroupBy(module => module.Id, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ModuleRegistryException(
                "OCWMOD001",
                $"Module ID '{duplicate.Key}' is declared more than once.");
        }

        _modules = catalog.ToDictionary(module => module.Id, StringComparer.Ordinal);

        foreach (var module in catalog.OrderBy(module => module.Id, StringComparer.Ordinal))
        {
            var missing = module.Dependencies
                .Order(StringComparer.Ordinal)
                .FirstOrDefault(dependency => !_modules.ContainsKey(dependency));
            if (missing is not null)
            {
                throw new ModuleRegistryException(
                    "OCWMOD002",
                    $"Module '{module.Id}' depends on missing module '{missing}'.");
            }
        }

        StartupOrder = BuildStartupOrder(catalog);
    }

    public IReadOnlyList<ModuleDescriptor> StartupOrder { get; }

    public ModuleDescriptor SelectPrimaryModule(string? preferredModuleId = null)
    {
        if (preferredModuleId is not null)
        {
            if (_modules.TryGetValue(preferredModuleId, out var preferred) &&
                preferred.CanBePrimaryHost)
            {
                return preferred;
            }

            throw InvalidPrimaryHost(preferredModuleId);
        }

        var candidates = _modules.Values
            .Where(module => module.CanBePrimaryHost)
            .OrderByDescending(module => module.Priority)
            .ThenBy(module => module.Id, StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length == 0)
        {
            throw InvalidPrimaryHost(null);
        }

        if (candidates.Length > 1 && candidates[0].Priority == candidates[1].Priority)
        {
            var tiedIds = string.Join(
                ", ",
                candidates
                    .TakeWhile(module => module.Priority == candidates[0].Priority)
                    .Select(module => module.Id));
            throw new ModuleRegistryException(
                "OCWMOD006",
                $"Primary host priority {candidates[0].Priority} is tied between: {tiedIds}.");
        }

        return candidates[0];
    }

    private static IReadOnlyList<ModuleDescriptor> BuildStartupOrder(
        IReadOnlyCollection<ModuleDescriptor> modules)
    {
        var byId = modules.ToDictionary(module => module.Id, StringComparer.Ordinal);
        var dependencyCounts = modules.ToDictionary(
            module => module.Id,
            module => module.Dependencies.Distinct(StringComparer.Ordinal).Count(),
            StringComparer.Ordinal);
        var dependents = modules.ToDictionary(
            module => module.Id,
            _ => new List<string>(),
            StringComparer.Ordinal);

        foreach (var module in modules)
        {
            foreach (var dependency in module.Dependencies.Distinct(StringComparer.Ordinal))
            {
                dependents[dependency].Add(module.Id);
            }
        }

        var ready = new SortedSet<string>(
            dependencyCounts
                .Where(pair => pair.Value == 0)
                .Select(pair => pair.Key),
            StringComparer.Ordinal);
        var ordered = new List<ModuleDescriptor>(modules.Count);

        while (ready.Count > 0)
        {
            var id = ready.Min!;
            ready.Remove(id);
            ordered.Add(byId[id]);

            foreach (var dependent in dependents[id].Order(StringComparer.Ordinal))
            {
                if (--dependencyCounts[dependent] == 0)
                {
                    ready.Add(dependent);
                }
            }
        }

        if (ordered.Count != modules.Count)
        {
            var unresolved = string.Join(
                ", ",
                dependencyCounts
                    .Where(pair => pair.Value > 0)
                    .Select(pair => pair.Key)
                    .Order(StringComparer.Ordinal));
            throw new ModuleRegistryException(
                "OCWMOD003",
                $"Module dependency cycle prevents resolving: {unresolved}.");
        }

        return ordered.AsReadOnly();
    }

    private ModuleRegistryException InvalidPrimaryHost(string? preferredModuleId)
    {
        var available = string.Join(
            ", ",
            _modules.Values
                .OrderBy(module => module.Id, StringComparer.Ordinal)
                .Select(module =>
                    $"{module.Id}(priority={module.Priority}, primary={module.CanBePrimaryHost})"));
        return new ModuleRegistryException(
            "OCWMOD005",
            $"No valid primary host was found for preferred module " +
            $"'{preferredModuleId ?? "<none>"}'. Available modules: {available}.");
    }

    private static bool IsLowerKebabCase(string value)
    {
        if (value.Length == 0 || value[0] == '-' || value[^1] == '-')
        {
            return false;
        }

        var previousWasHyphen = false;
        foreach (var character in value)
        {
            if (character == '-')
            {
                if (previousWasHyphen)
                {
                    return false;
                }

                previousWasHyphen = true;
                continue;
            }

            if ((character < 'a' || character > 'z') &&
                (character < '0' || character > '9'))
            {
                return false;
            }

            previousWasHyphen = false;
        }

        return true;
    }
}

public sealed class ModuleRegistryException : InvalidOperationException
{
    public ModuleRegistryException(string code, string message)
        : base($"{code}: {message}")
    {
        Code = code;
    }

    public string Code { get; }
}
