using System.Xml.Linq;
using Xunit;

namespace OpenCoWork.ArchitectureTests;

public sealed class ProjectGraphTests
{
    private static readonly ProjectContract[] FrozenProjects =
    [
        new("OpenCoWork.Abstractions", "net10.0", "Library", [], []),
        new("OpenCoWork.Core", "net10.0", "Library", ["OpenCoWork.Abstractions"], ["OpenCoWork.Generators"]),
        new("OpenCoWork.Protocol", "net10.0", "Library", ["OpenCoWork.Abstractions"], ["OpenCoWork.Generators"]),
        new(
            "OpenCoWork.Automations",
            "net10.0",
            "Library",
            ["OpenCoWork.Abstractions", "OpenCoWork.Protocol"],
            ["OpenCoWork.Generators"]),
        new(
            "OpenCoWork.Teams",
            "net10.0",
            "Library",
            ["OpenCoWork.Abstractions", "OpenCoWork.Protocol"],
            ["OpenCoWork.Generators"]),
        new(
            "OpenCoWork.App",
            "net10.0",
            "Exe",
            [
                "OpenCoWork.Automations",
                "OpenCoWork.Core",
                "OpenCoWork.Protocol",
                "OpenCoWork.Teams",
            ],
            ["OpenCoWork.Generators"],
            "opencowork"),
        new("OpenCoWork.Generators", "netstandard2.0", "Library", [], []),
        new("OpenCoWork.McpFixture", "net10.0", "Exe", [], []),
        new(
            "OpenCoWork.PluginFixture",
            "net10.0",
            "Library",
            ["OpenCoWork.Abstractions"],
            []),
        new(
            "OpenCoWork.Core.Tests",
            "net10.0",
            "Exe",
            ["OpenCoWork.Core", "OpenCoWork.PluginFixture"],
            []),
        new("OpenCoWork.Protocol.Tests", "net10.0", "Exe", ["OpenCoWork.Protocol"], []),
        new("OpenCoWork.Generators.Tests", "net10.0", "Exe", ["OpenCoWork.Generators"], []),
        new("OpenCoWork.ArchitectureTests", "net10.0", "Exe", [], []),
        new(
            "OpenCoWork.IntegrationTests",
            "net10.0",
            "Exe",
            ["OpenCoWork.App", "OpenCoWork.McpFixture"],
            []),
        new("OpenCoWork.Protocol.TestClient", "net10.0", "Exe", ["OpenCoWork.Protocol"], []),
    ];

    [Fact]
    public void Repository_project_graph_matches_frozen_contract()
    {
        var repositoryRoot = FindRepositoryRoot();
        var projects = LoadProjects(repositoryRoot);
        var expectedNames = FrozenProjects.Select(project => project.Name).Order().ToArray();

        Assert.Equal(expectedNames, projects.Keys.Order().ToArray());
        Assert.Equal(expectedNames, LoadSolutionProjects(repositoryRoot));

        foreach (var contract in FrozenProjects)
        {
            var project = projects[contract.Name];

            Assert.Equal(contract.TargetFramework, project.TargetFramework);
            Assert.Equal(contract.OutputType, project.OutputType);
            Assert.Equal(contract.AssemblyName ?? contract.Name, project.AssemblyName);
            Assert.Equal(contract.AnalyzerReferences.Order(), project.AnalyzerReferences.Order());
            Assert.Empty(project.InvalidAnalyzerReferences);
        }

        Assert.Empty(FindReferenceMismatches(projects));
        AssertBuildFilesUseOpenCoWorkBrand(repositoryRoot, projects.Values);
    }

    [Fact]
    public void Unexpected_reference_is_rejected()
    {
        var projects = FrozenProjects.ToDictionary(
            contract => contract.Name,
            contract => new ProjectModel(
                contract.Name,
                contract.TargetFramework,
                contract.OutputType,
                contract.AssemblyName ?? contract.Name,
                [.. contract.ProjectReferences],
                [.. contract.AnalyzerReferences],
                []));

        projects["OpenCoWork.Protocol"].ProjectReferences.Add("OpenCoWork.Core");

        var errors = FindReferenceMismatches(projects);

        Assert.Contains(
            errors,
            error => error.Contains(
                "OpenCoWork.Protocol -> OpenCoWork.Core",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Only_app_enables_runtime_catalog_generation()
    {
        var repositoryRoot = FindRepositoryRoot();
        var projectFiles = Directory.EnumerateFiles(
            Path.Combine(repositoryRoot, "src"),
            "*.csproj",
            SearchOption.AllDirectories);
        var aggregators = projectFiles
            .Where(path => string.Equals(
                ReadProperty(XDocument.Load(path), "OpenCoWorkGenerateCatalog"),
                "true",
                StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetFileNameWithoutExtension(path)!)
            .Order()
            .ToArray();

        Assert.Equal(["OpenCoWork.App"], aggregators);

        var appProject = XDocument.Load(Path.Combine(
            repositoryRoot,
            "src",
            "OpenCoWork.App",
            "OpenCoWork.App.csproj"));
        Assert.Contains(
            appProject.Descendants(),
            element =>
                element.Name.LocalName == "CompilerVisibleProperty" &&
                string.Equals(
                    element.Attribute("Include")?.Value,
                    "OpenCoWorkGenerateCatalog",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void Tiktoken_is_frozen_and_referenced_only_by_core()
    {
        var repositoryRoot = FindRepositoryRoot();
        var references = Directory
            .EnumerateFiles(
                Path.Combine(repositoryRoot, "src"),
                "*.csproj",
                SearchOption.AllDirectories)
            .Where(path => XDocument.Load(path)
                .Descendants()
                .Any(element =>
                    element.Name.LocalName == "PackageReference" &&
                    string.Equals(
                        element.Attribute("Include")?.Value,
                        "Tiktoken",
                        StringComparison.Ordinal)))
            .Select(path => Path.GetFileNameWithoutExtension(path)!)
            .ToArray();
        var packageVersions = XDocument
            .Load(Path.Combine(repositoryRoot, "Directory.Packages.props"))
            .Descendants()
            .Where(element =>
                element.Name.LocalName == "PackageVersion" &&
                string.Equals(
                    element.Attribute("Include")?.Value,
                    "Tiktoken",
                    StringComparison.Ordinal))
            .Select(element => element.Attribute("Version")!.Value)
            .ToArray();

        Assert.Equal(["OpenCoWork.Core"], references);
        Assert.Equal(["3.1.5"], packageVersions);
    }

    [Fact]
    public void JsonSchemaNet_is_the_only_M4_dependency_and_is_referenced_only_by_core()
    {
        var repositoryRoot = FindRepositoryRoot();
        var references = Directory
            .EnumerateFiles(
                Path.Combine(repositoryRoot, "src"),
                "*.csproj",
                SearchOption.AllDirectories)
            .Where(path => XDocument.Load(path)
                .Descendants()
                .Any(element =>
                    element.Name.LocalName == "PackageReference" &&
                    string.Equals(
                        element.Attribute("Include")?.Value,
                        "JsonSchema.Net",
                        StringComparison.Ordinal)))
            .Select(path => Path.GetFileNameWithoutExtension(path)!)
            .ToArray();
        var packageVersions = XDocument
            .Load(Path.Combine(repositoryRoot, "Directory.Packages.props"))
            .Descendants()
            .Where(element =>
                element.Name.LocalName == "PackageVersion" &&
                string.Equals(
                    element.Attribute("Include")?.Value,
                    "JsonSchema.Net",
                    StringComparison.Ordinal))
            .Select(element => element.Attribute("Version")!.Value)
            .ToArray();

        Assert.Equal(["OpenCoWork.Core"], references);
        Assert.Equal(["9.4.0"], packageVersions);
    }

    private static Dictionary<string, ProjectModel> LoadProjects(string repositoryRoot)
    {
        return new[] { "src", "tests" }
            .SelectMany(directory => Directory.EnumerateFiles(
                Path.Combine(repositoryRoot, directory),
                "*.csproj",
                SearchOption.AllDirectories))
            .Select(path => LoadProject(repositoryRoot, path))
            .ToDictionary(project => project.Name, StringComparer.Ordinal);
    }

    private static ProjectModel LoadProject(string repositoryRoot, string projectPath)
    {
        var document = XDocument.Load(projectPath);
        var projectName = Path.GetFileNameWithoutExtension(projectPath);
        var targetFramework = ReadProperty(document, "TargetFramework")
            ?? ReadProperty(
                XDocument.Load(Path.Combine(repositoryRoot, "Directory.Build.props")),
                "TargetFramework")
            ?? throw new InvalidDataException($"{projectName} has no TargetFramework.");
        var projectReferences = new List<string>();
        var analyzerReferences = new List<string>();
        var invalidAnalyzerReferences = new List<string>();

        foreach (var reference in document
                     .Descendants()
                     .Where(element => element.Name.LocalName == "ProjectReference"))
        {
            var include = reference.Attribute("Include")?.Value
                ?? throw new InvalidDataException($"{projectName} has a ProjectReference without Include.");
            var referencedProject = Path.GetFileNameWithoutExtension(
                include.Replace('\\', Path.DirectorySeparatorChar));
            var outputItemType = ReadMetadata(reference, "OutputItemType");
            var referenceOutputAssembly = ReadMetadata(reference, "ReferenceOutputAssembly");
            var isAnalyzer = string.Equals(
                outputItemType,
                "Analyzer",
                StringComparison.OrdinalIgnoreCase);
            var excludesRuntime = string.Equals(
                referenceOutputAssembly,
                "false",
                StringComparison.OrdinalIgnoreCase);

            if (isAnalyzer || excludesRuntime)
            {
                analyzerReferences.Add(referencedProject);

                if (!isAnalyzer || !excludesRuntime)
                {
                    invalidAnalyzerReferences.Add(referencedProject);
                }

                continue;
            }

            projectReferences.Add(referencedProject);
        }

        return new ProjectModel(
            projectName,
            targetFramework,
            ReadProperty(document, "OutputType") ?? "Library",
            ReadProperty(document, "AssemblyName") ?? projectName,
            projectReferences,
            analyzerReferences,
            invalidAnalyzerReferences);
    }

    private static string[] LoadSolutionProjects(string repositoryRoot)
    {
        return XDocument
            .Load(Path.Combine(repositoryRoot, "OpenCoWork.slnx"))
            .Descendants()
            .Where(element => element.Name.LocalName == "Project")
            .Select(element => element.Attribute("Path")?.Value)
            .Select(path => Path.GetFileNameWithoutExtension(
                path ?? throw new InvalidDataException("Solution Project is missing Path.")))
            .Order()
            .ToArray();
    }

    private static string[] FindReferenceMismatches(
        IReadOnlyDictionary<string, ProjectModel> projects)
    {
        var errors = new List<string>();

        foreach (var contract in FrozenProjects)
        {
            if (!projects.TryGetValue(contract.Name, out var project))
            {
                errors.Add($"Missing project: {contract.Name}");
                continue;
            }

            errors.AddRange(project.ProjectReferences
                .Except(contract.ProjectReferences, StringComparer.Ordinal)
                .Select(reference => $"Unexpected reference: {contract.Name} -> {reference}"));
            errors.AddRange(contract.ProjectReferences
                .Except(project.ProjectReferences, StringComparer.Ordinal)
                .Select(reference => $"Missing reference: {contract.Name} -> {reference}"));
        }

        return [.. errors];
    }

    private static void AssertBuildFilesUseOpenCoWorkBrand(
        string repositoryRoot,
        IEnumerable<ProjectModel> projects)
    {
        var files = projects
            .Select(project => project.Path)
            .Append(Path.Combine(repositoryRoot, "OpenCoWork.slnx"))
            .Append(Path.Combine(repositoryRoot, "Directory.Build.props"))
            .Append(Path.Combine(repositoryRoot, "Directory.Packages.props"));

        foreach (var file in files)
        {
            var content = File.ReadAllText(file);

            Assert.False(
                content.Contains("DotCraft", StringComparison.OrdinalIgnoreCase),
                $"{file} contains the DotCraft brand.");
            Assert.False(
                content.Contains(".craft", StringComparison.OrdinalIgnoreCase),
                $"{file} contains the .craft compatibility marker.");
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OpenCoWork.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not find OpenCoWork.slnx above {AppContext.BaseDirectory}.");
    }

    private static string? ReadProperty(XDocument document, string name)
    {
        return document
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == name)
            ?.Value;
    }

    private static string? ReadMetadata(XElement element, string name)
    {
        return element.Attribute(name)?.Value
            ?? element.Elements().FirstOrDefault(child => child.Name.LocalName == name)?.Value;
    }

    private sealed record ProjectContract(
        string Name,
        string TargetFramework,
        string OutputType,
        string[] ProjectReferences,
        string[] AnalyzerReferences,
        string? AssemblyName = null);

    private sealed record ProjectModel(
        string Name,
        string TargetFramework,
        string OutputType,
        string AssemblyName,
        List<string> ProjectReferences,
        List<string> AnalyzerReferences,
        List<string> InvalidAnalyzerReferences)
    {
        public string Path { get; } = System.IO.Path.Combine(
            FindRepositoryRoot(),
            Name.Contains("Tests", StringComparison.Ordinal) &&
            !Name.EndsWith("TestClient", StringComparison.Ordinal)
                ? "tests"
                : Name is
                    "OpenCoWork.McpFixture" or
                    "OpenCoWork.PluginFixture" or
                    "OpenCoWork.Protocol.TestClient"
                    ? "tests"
                    : "src",
            Name,
            $"{Name}.csproj");
    }
}
