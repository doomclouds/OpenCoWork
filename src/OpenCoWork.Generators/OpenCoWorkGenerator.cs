using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace OpenCoWork.Generators;

[Generator(LanguageNames.CSharp)]
public sealed class OpenCoWorkGenerator : IIncrementalGenerator
{
    private const string ModuleAttributeName =
        "OpenCoWork.Abstractions.OpenCoWorkModuleAttribute";
    private const string ConfigSectionAttributeName =
        "OpenCoWork.Abstractions.ConfigSectionAttribute";
    private const string SecretAttributeName =
        "OpenCoWork.Abstractions.SecretAttribute";
    private const string WireMethodAttributeName =
        "OpenCoWork.Abstractions.OpenCoWorkWireMethodAttribute";
    private const string RequiredAttributeName =
        "System.ComponentModel.DataAnnotations.RequiredAttribute";
    private const string RegularExpressionAttributeName =
        "System.ComponentModel.DataAnnotations.RegularExpressionAttribute";
    private const string RangeAttributeName =
        "System.ComponentModel.DataAnnotations.RangeAttribute";
    private const string DiagnosticCategory = "OpenCoWork.Generators";

    private static readonly DiagnosticDescriptor InvalidModuleId = new(
        "OCWGEN001",
        "Invalid module ID",
        "Module ID '{0}' must use lower kebab-case",
        DiagnosticCategory,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DuplicateModuleId = new(
        "OCWGEN002",
        "Duplicate module ID",
        "Module ID '{0}' is declared by multiple types: {1}",
        DiagnosticCategory,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor MissingModuleDependency = new(
        "OCWGEN003",
        "Missing module dependency",
        "Module '{0}' depends on missing module '{1}'",
        DiagnosticCategory,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ModuleDependencyCycle = new(
        "OCWGEN004",
        "Module dependency cycle",
        "Module dependency cycle prevents resolving: {0}",
        DiagnosticCategory,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidConfigSectionName = new(
        "OCWGEN005",
        "Invalid config section name",
        "Config section name '{0}' must use lowerCamelCase",
        DiagnosticCategory,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DuplicateConfigSection = new(
        "OCWGEN006",
        "Duplicate config section",
        "Config section '{0}' is declared by multiple types: {1}",
        DiagnosticCategory,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DuplicateWireMethod = new(
        "OCWGEN007",
        "Duplicate Wire method",
        "Wire method '{0}' is declared by multiple members: {1}",
        DiagnosticCategory,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidCatalogDeclaration = new(
        "OCWGEN008",
        "Invalid catalog declaration",
        "Catalog declaration '{0}' is invalid: {1}",
        DiagnosticCategory,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var input = context.CompilationProvider.Combine(context.AnalyzerConfigOptionsProvider);

        context.RegisterSourceOutput(
            input,
            static (productionContext, pair) =>
                Execute(pair.Left, pair.Right, productionContext));
    }

    private static void Execute(
        Compilation compilation,
        AnalyzerConfigOptionsProvider options,
        SourceProductionContext context)
    {
        var generateCatalog = options.GlobalOptions.TryGetValue(
            "build_property.OpenCoWorkGenerateCatalog",
            out var value) &&
            bool.TryParse(value, out var enabled) &&
            enabled;
        var assemblies = new List<IAssemblySymbol> { compilation.Assembly };

        if (generateCatalog)
        {
            assemblies.AddRange(compilation.SourceModule.ReferencedAssemblySymbols
                .Where(UsesOpenCoWorkContracts));
        }

        var declarations = CollectDeclarations(assemblies);
        var diagnostics = ValidateDeclarations(declarations, generateCatalog);

        foreach (var diagnostic in diagnostics)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                diagnostic.Descriptor,
                GetLocation(diagnostic.Symbol),
                diagnostic.Arguments));
        }

        if (!generateCatalog)
        {
            return;
        }

        context.AddSource(
            "RuntimeCatalog.Modules.g.cs",
            SourceText.From(
                RenderModules(
                    declarations.Modules
                        .Where(module => IsValidModuleType(module.Type))
                        .ToImmutableArray()),
                Encoding.UTF8));
        context.AddSource(
            "RuntimeCatalog.Config.g.cs",
            SourceText.From(
                RenderConfigSections(
                    declarations.ConfigSections
                        .Where(section => IsValidConfigType(section.Type))
                        .ToImmutableArray()),
                Encoding.UTF8));
        context.AddSource(
            "RuntimeCatalog.Wire.g.cs",
            SourceText.From(RenderWireMethods(declarations.WireMethods), Encoding.UTF8));
    }

    private static bool UsesOpenCoWorkContracts(IAssemblySymbol assembly)
    {
        return string.Equals(
                   assembly.Name,
                   "OpenCoWork.Abstractions",
                   StringComparison.Ordinal) ||
               assembly.Modules
                   .SelectMany(module => module.ReferencedAssemblySymbols)
                   .Any(reference => string.Equals(
                       reference.Name,
                       "OpenCoWork.Abstractions",
                       StringComparison.Ordinal));
    }

    private static Declarations CollectDeclarations(IEnumerable<IAssemblySymbol> assemblies)
    {
        var modules = new List<ModuleModel>();
        var configSections = new List<ConfigSectionModel>();
        var wireMethods = new List<WireMethodModel>();
        var invalidDeclarations = new List<InvalidDeclarationModel>();

        foreach (var assembly in assemblies
                     .OrderBy(symbol => symbol.Name, StringComparer.Ordinal))
        {
            foreach (var type in EnumerateTypes(assembly.GlobalNamespace))
            {
                var moduleAttribute = FindAttribute(type, ModuleAttributeName);

                if (moduleAttribute is not null)
                {
                    if (TryReadString(moduleAttribute, 0, out var moduleId))
                    {
                        modules.Add(new ModuleModel(
                            type,
                            moduleId,
                            ReadStringArray(moduleAttribute, "Dependencies"),
                            ReadInt32(moduleAttribute, "Priority"),
                            ReadBoolean(moduleAttribute, "CanBePrimaryHost")));
                    }
                    else
                    {
                        invalidDeclarations.Add(new InvalidDeclarationModel(
                            type,
                            "module ID is missing or cannot be resolved"));
                    }
                }

                var configAttribute = FindAttribute(type, ConfigSectionAttributeName);

                if (configAttribute is not null)
                {
                    if (TryReadString(configAttribute, 0, out var sectionName))
                    {
                        configSections.Add(new ConfigSectionModel(type, sectionName));
                    }
                    else
                    {
                        invalidDeclarations.Add(new InvalidDeclarationModel(
                            type,
                            "config section name is missing or cannot be resolved"));
                    }
                }

                foreach (var method in type.GetMembers()
                             .OfType<IMethodSymbol>()
                             .OrderBy(symbol => symbol.Name, StringComparer.Ordinal))
                {
                    var wireAttribute = FindAttribute(method, WireMethodAttributeName);

                    if (wireAttribute is not null)
                    {
                        if (TryReadString(wireAttribute, 0, out var wireMethod))
                        {
                            wireMethods.Add(new WireMethodModel(method, wireMethod));
                        }
                        else
                        {
                            invalidDeclarations.Add(new InvalidDeclarationModel(
                                method,
                                "Wire method name is missing or cannot be resolved"));
                        }
                    }
                }
            }
        }

        return new Declarations(
            modules
                .OrderBy(model => model.Id, StringComparer.Ordinal)
                .ThenBy(model => GetTypeName(model.Type), StringComparer.Ordinal)
                .ToImmutableArray(),
            configSections
                .OrderBy(model => model.Name, StringComparer.Ordinal)
                .ThenBy(model => GetTypeName(model.Type), StringComparer.Ordinal)
                .ToImmutableArray(),
            wireMethods
                .OrderBy(model => model.Method, StringComparer.Ordinal)
                .ThenBy(
                    model => GetTypeName(model.Symbol.ContainingType),
                    StringComparer.Ordinal)
                .ThenBy(model => model.Symbol.Name, StringComparer.Ordinal)
                .ToImmutableArray(),
            invalidDeclarations
                .OrderBy(
                    model => model.Symbol.ToDisplayString(),
                    StringComparer.Ordinal)
                .ToImmutableArray());
    }

    private static ImmutableArray<PendingDiagnostic> ValidateDeclarations(
        Declarations declarations,
        bool validateDependencyGraph)
    {
        var diagnostics = new List<PendingDiagnostic>();

        foreach (var declaration in declarations.InvalidDeclarations)
        {
            diagnostics.Add(new PendingDiagnostic(
                InvalidCatalogDeclaration,
                declaration.Symbol,
                declaration.Symbol.ToDisplayString(),
                declaration.Symbol.ToDisplayString(),
                declaration.Reason));
        }

        foreach (var module in declarations.Modules)
        {
            if (!IsLowerKebabCase(module.Id))
            {
                diagnostics.Add(new PendingDiagnostic(
                    InvalidModuleId,
                    module.Type,
                    module.Id,
                    module.Id));
            }

            foreach (var dependency in module.Dependencies)
            {
                if (!IsLowerKebabCase(dependency))
                {
                    diagnostics.Add(new PendingDiagnostic(
                        InvalidModuleId,
                        module.Type,
                        module.Id + "\0" + dependency,
                        dependency));
                }
            }

            if (!IsValidModuleType(module.Type))
            {
                diagnostics.Add(new PendingDiagnostic(
                    InvalidCatalogDeclaration,
                    module.Type,
                    GetTypeName(module.Type),
                    GetTypeName(module.Type),
                    "module types must be public and non-abstract"));
            }
        }

        foreach (var group in declarations.Modules
                     .GroupBy(module => module.Id, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            var types = string.Join(
                ", ",
                group.Select(module => GetTypeName(module.Type))
                    .OrderBy(name => name, StringComparer.Ordinal));
            var first = group.OrderBy(
                module => GetTypeName(module.Type),
                StringComparer.Ordinal).First();
            diagnostics.Add(new PendingDiagnostic(
                DuplicateModuleId,
                first.Type,
                group.Key,
                group.Key,
                types));
        }

        foreach (var section in declarations.ConfigSections)
        {
            if (!IsLowerCamelCase(section.Name))
            {
                diagnostics.Add(new PendingDiagnostic(
                    InvalidConfigSectionName,
                    section.Type,
                    section.Name,
                    section.Name));
            }

            if (!IsValidConfigType(section.Type))
            {
                diagnostics.Add(new PendingDiagnostic(
                    InvalidCatalogDeclaration,
                    section.Type,
                    GetTypeName(section.Type),
                    GetTypeName(section.Type),
                    "config section types must be public, non-abstract, and have a public parameterless constructor"));
            }
        }

        foreach (var group in declarations.ConfigSections
                     .GroupBy(section => section.Name, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            var types = string.Join(
                ", ",
                group.Select(section => GetTypeName(section.Type))
                    .OrderBy(name => name, StringComparer.Ordinal));
            var first = group.OrderBy(
                section => GetTypeName(section.Type),
                StringComparer.Ordinal).First();
            diagnostics.Add(new PendingDiagnostic(
                DuplicateConfigSection,
                first.Type,
                group.Key,
                group.Key,
                types));
        }

        foreach (var group in declarations.WireMethods
                     .GroupBy(method => method.Method, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            var members = string.Join(
                ", ",
                group.Select(method =>
                        GetTypeName(method.Symbol.ContainingType) + "." + method.Symbol.Name)
                    .OrderBy(name => name, StringComparer.Ordinal));
            var first = group.OrderBy(
                method => GetTypeName(method.Symbol.ContainingType) + "." + method.Symbol.Name,
                StringComparer.Ordinal).First();
            diagnostics.Add(new PendingDiagnostic(
                DuplicateWireMethod,
                first.Symbol,
                group.Key,
                group.Key,
                members));
        }

        if (validateDependencyGraph)
        {
            ValidateDependencyGraph(declarations.Modules, diagnostics);
        }

        return diagnostics
            .OrderBy(diagnostic => diagnostic.Descriptor.Id, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.SortKey, StringComparer.Ordinal)
            .ThenBy(
                diagnostic => GetLocation(diagnostic.Symbol).SourceTree?.FilePath,
                StringComparer.Ordinal)
            .ThenBy(diagnostic => GetLocation(diagnostic.Symbol).SourceSpan.Start)
            .ToImmutableArray();
    }

    private static void ValidateDependencyGraph(
        ImmutableArray<ModuleModel> modules,
        List<PendingDiagnostic> diagnostics)
    {
        var uniqueModules = modules
            .GroupBy(module => module.Id, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .Select(group => group.Single())
            .ToDictionary(module => module.Id, StringComparer.Ordinal);

        foreach (var module in uniqueModules.Values.OrderBy(
                     module => module.Id,
                     StringComparer.Ordinal))
        {
            foreach (var dependency in module.Dependencies)
            {
                if (IsLowerKebabCase(dependency) &&
                    !uniqueModules.ContainsKey(dependency))
                {
                    diagnostics.Add(new PendingDiagnostic(
                        MissingModuleDependency,
                        module.Type,
                        module.Id + "\0" + dependency,
                        module.Id,
                        dependency));
                }
            }
        }

        var remaining = new HashSet<string>(uniqueModules.Keys, StringComparer.Ordinal);
        var removedAny = true;

        while (removedAny)
        {
            removedAny = false;

            foreach (var id in remaining.OrderBy(value => value, StringComparer.Ordinal).ToArray())
            {
                if (uniqueModules[id].Dependencies.All(dependency => !remaining.Contains(dependency)))
                {
                    remaining.Remove(id);
                    removedAny = true;
                }
            }
        }

        if (remaining.Count > 0)
        {
            var ids = remaining.OrderBy(id => id, StringComparer.Ordinal).ToArray();
            diagnostics.Add(new PendingDiagnostic(
                ModuleDependencyCycle,
                uniqueModules[ids[0]].Type,
                ids[0],
                string.Join(", ", ids)));
        }
    }

    private static bool IsValidConfigType(INamedTypeSymbol type)
    {
        return type.TypeKind == TypeKind.Class &&
               type.IsRecord &&
               type.DeclaredAccessibility == Accessibility.Public &&
               !type.IsAbstract &&
               type.GetMembers()
                   .OfType<IPropertySymbol>()
                   .Where(property =>
                       !property.IsStatic &&
                       property.DeclaredAccessibility == Accessibility.Public)
                   .All(property =>
                       property.SetMethod is null ||
                       property.SetMethod.IsInitOnly) &&
               type.InstanceConstructors.Any(constructor =>
                   constructor.Parameters.Length == 0 &&
                   constructor.DeclaredAccessibility == Accessibility.Public);
    }

    private static bool IsValidModuleType(INamedTypeSymbol type)
    {
        return type.TypeKind == TypeKind.Class &&
               type.DeclaredAccessibility == Accessibility.Public &&
               !type.IsAbstract;
    }

    private static bool IsLowerKebabCase(string value)
    {
        if (value.Length == 0 || value[0] == '-' || value[value.Length - 1] == '-')
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

    private static bool IsLowerCamelCase(string value)
    {
        return value.Length > 0 &&
               value[0] >= 'a' &&
               value[0] <= 'z' &&
               value.All(character =>
                   (character >= 'a' && character <= 'z') ||
                   (character >= 'A' && character <= 'Z') ||
                   (character >= '0' && character <= '9'));
    }

    private static Location GetLocation(ISymbol symbol)
    {
        return symbol.Locations.FirstOrDefault(location => location.IsInSource)
               ?? Location.None;
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateTypes(INamespaceSymbol @namespace)
    {
        foreach (var type in @namespace.GetTypeMembers()
                     .OrderBy(symbol => symbol.MetadataName, StringComparer.Ordinal))
        {
            foreach (var nestedType in EnumerateTypes(type))
            {
                yield return nestedType;
            }
        }

        foreach (var childNamespace in @namespace.GetNamespaceMembers()
                     .OrderBy(symbol => symbol.Name, StringComparer.Ordinal))
        {
            foreach (var type in EnumerateTypes(childNamespace))
            {
                yield return type;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateTypes(INamedTypeSymbol type)
    {
        yield return type;

        foreach (var nestedType in type.GetTypeMembers()
                     .OrderBy(symbol => symbol.MetadataName, StringComparer.Ordinal))
        {
            foreach (var descendant in EnumerateTypes(nestedType))
            {
                yield return descendant;
            }
        }
    }

    private static AttributeData? FindAttribute(ISymbol symbol, string metadataName)
    {
        return symbol.GetAttributes().FirstOrDefault(attribute => string.Equals(
            attribute.AttributeClass?.ToDisplayString(),
            metadataName,
            StringComparison.Ordinal));
    }

    private static bool TryReadString(
        AttributeData attribute,
        int argumentIndex,
        out string value)
    {
        if (attribute.ConstructorArguments.Length > argumentIndex &&
            attribute.ConstructorArguments[argumentIndex].Value is string text)
        {
            value = text;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryReadJsonNumber(
        AttributeData attribute,
        int argumentIndex,
        out string value)
    {
        if (attribute.ConstructorArguments.Length > argumentIndex &&
            attribute.ConstructorArguments[argumentIndex].Value is IFormattable number &&
            attribute.ConstructorArguments[argumentIndex].Value is
                byte or sbyte or short or ushort or int or uint or long or ulong or
                float or double or decimal)
        {
            value = number.ToString(
                null,
                global::System.Globalization.CultureInfo.InvariantCulture);
            return value != "NaN" &&
                   value != "Infinity" &&
                   value != "-Infinity";
        }

        value = string.Empty;
        return false;
    }

    private static ImmutableArray<string> ReadStringArray(
        AttributeData attribute,
        string name)
    {
        foreach (var pair in attribute.NamedArguments)
        {
            if (string.Equals(pair.Key, name, StringComparison.Ordinal) &&
                pair.Value.Kind == TypedConstantKind.Array)
            {
                return pair.Value.Values
                    .Select(value => value.Value as string)
                    .Where(value => value is not null)
                    .Cast<string>()
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToImmutableArray();
            }
        }

        return ImmutableArray<string>.Empty;
    }

    private static int ReadInt32(AttributeData attribute, string name)
    {
        foreach (var pair in attribute.NamedArguments)
        {
            if (string.Equals(pair.Key, name, StringComparison.Ordinal) &&
                pair.Value.Value is int value)
            {
                return value;
            }
        }

        return 0;
    }

    private static bool ReadBoolean(AttributeData attribute, string name)
    {
        foreach (var pair in attribute.NamedArguments)
        {
            if (string.Equals(pair.Key, name, StringComparison.Ordinal) &&
                pair.Value.Value is bool value)
            {
                return value;
            }
        }

        return false;
    }

    private static string RenderModules(ImmutableArray<ModuleModel> modules)
    {
        var builder = CreateCatalogBuilder();
        builder.AppendLine(
            "    internal static global::System.Collections.Generic.IReadOnlyList<global::OpenCoWork.Abstractions.ModuleDescriptor> Modules { get; } =");
        builder.AppendLine(
            "        new global::OpenCoWork.Abstractions.ModuleDescriptor[]");
        builder.AppendLine("        {");

        foreach (var module in modules)
        {
            builder.AppendLine(
                "            new global::OpenCoWork.Abstractions.ModuleDescriptor(");
            builder.Append("                typeof(")
                .Append(GetTypeName(module.Type))
                .AppendLine("),");
            builder.Append("                ")
                .Append(ToLiteral(module.Id))
                .AppendLine(",");
            builder.Append("                new string[] {");

            if (module.Dependencies.Length > 0)
            {
                builder.Append(' ')
                    .Append(string.Join(
                        ", ",
                        module.Dependencies.Select(ToLiteral)))
                    .Append(' ');
            }

            builder.AppendLine("},");
            builder.Append("                ")
                .Append(module.Priority)
                .AppendLine(",");
            builder.Append("                ")
                .Append(module.CanBePrimaryHost ? "true" : "false")
                .AppendLine("),");
        }

        CloseCatalog(builder);
        return NormalizeNewlines(builder.ToString());
    }

    private static string RenderConfigSections(
        ImmutableArray<ConfigSectionModel> configSections)
    {
        var builder = CreateCatalogBuilder();
        builder.AppendLine(
            "    internal static global::System.Collections.Generic.IReadOnlyList<global::OpenCoWork.Abstractions.ConfigSectionDescriptor> ConfigSections { get; } =");
        builder.AppendLine(
            "        new global::OpenCoWork.Abstractions.ConfigSectionDescriptor[]");
        builder.AppendLine("        {");

        foreach (var section in configSections)
        {
            var typeName = GetTypeName(section.Type);
            var schema = BuildObjectSchema(section.Type, new HashSet<string>(StringComparer.Ordinal));

            builder.AppendLine(
                "            new global::OpenCoWork.Abstractions.ConfigSectionDescriptor(");
            builder.Append("                ")
                .Append(ToLiteral(section.Name))
                .AppendLine(",");
            builder.Append("                typeof(")
                .Append(typeName)
                .AppendLine("),");
            builder.Append("                static () => new ")
                .Append(typeName)
                .AppendLine("(),");
            builder.Append("                ")
                .Append(ToLiteral(schema))
                .AppendLine("),");
        }

        CloseCatalog(builder);
        return NormalizeNewlines(builder.ToString());
    }

    private static string RenderWireMethods(ImmutableArray<WireMethodModel> wireMethods)
    {
        var builder = CreateCatalogBuilder();
        builder.AppendLine(
            "    internal sealed record WireMethodDescriptor(string Method, string ContainingType, string MemberName);");
        builder.AppendLine();
        builder.AppendLine(
            "    internal static global::System.Collections.Generic.IReadOnlyList<WireMethodDescriptor> WireMethods { get; } =");
        builder.AppendLine("        new WireMethodDescriptor[]");
        builder.AppendLine("        {");

        foreach (var wireMethod in wireMethods)
        {
            builder.Append("            new WireMethodDescriptor(")
                .Append(ToLiteral(wireMethod.Method))
                .Append(", ")
                .Append(ToLiteral(wireMethod.Symbol.ContainingType.ToDisplayString()))
                .Append(", ")
                .Append(ToLiteral(wireMethod.Symbol.Name))
                .AppendLine("),");
        }

        CloseCatalog(builder);
        return NormalizeNewlines(builder.ToString());
    }

    private static StringBuilder CreateCatalogBuilder()
    {
        return new StringBuilder()
            .AppendLine("// <auto-generated/>")
            .AppendLine("#nullable enable")
            .AppendLine()
            .AppendLine("namespace OpenCoWork.Generated;")
            .AppendLine()
            .AppendLine("internal static partial class RuntimeCatalog")
            .AppendLine("{");
    }

    private static void CloseCatalog(StringBuilder builder)
    {
        builder.AppendLine("        };");
        builder.AppendLine("}");
    }

    private static string BuildObjectSchema(
        INamedTypeSymbol type,
        HashSet<string> visitedTypes)
    {
        var typeName = type.ToDisplayString();

        if (!visitedTypes.Add(typeName))
        {
            return "{\"type\":\"object\"}";
        }

        var properties = type.GetMembers()
            .OfType<IPropertySymbol>()
            .Where(property =>
                !property.IsStatic &&
                property.GetMethod is not null &&
                property.DeclaredAccessibility == Accessibility.Public)
            .OrderBy(property => ToCamelCase(property.Name), StringComparer.Ordinal)
            .ToArray();
        var builder = new StringBuilder("{\"type\":\"object\",\"properties\":{");

        for (var index = 0; index < properties.Length; index++)
        {
            var property = properties[index];

            if (index > 0)
            {
                builder.Append(',');
            }

            builder.Append('"')
                .Append(EscapeJson(ToCamelCase(property.Name)))
                .Append("\":")
                .Append(BuildPropertySchema(property, visitedTypes));
        }

        builder.Append('}');

        var required = properties
            .Where(property => FindAttribute(property, RequiredAttributeName) is not null)
            .Select(property => ToCamelCase(property.Name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (required.Length > 0)
        {
            builder.Append(",\"required\":[")
                .Append(string.Join(
                    ",",
                    required.Select(name => "\"" + EscapeJson(name) + "\"")))
                .Append(']');
        }

        builder.Append(",\"additionalProperties\":false}");
        visitedTypes.Remove(typeName);
        return builder.ToString();
    }

    private static string BuildPropertySchema(
        IPropertySymbol property,
        HashSet<string> visitedTypes)
    {
        var type = UnwrapNullable(property.Type);
        string schema;

        if (type.SpecialType == SpecialType.System_String)
        {
            schema = "{\"type\":\"string\"";
            var pattern = FindAttribute(property, RegularExpressionAttributeName);

            if (pattern is not null && TryReadString(pattern, 0, out var expression))
            {
                schema += ",\"pattern\":\"" + EscapeJson(expression) + "\"";
            }

            schema += "}";
        }
        else if (type.SpecialType == SpecialType.System_Boolean)
        {
            schema = "{\"type\":\"boolean\"}";
        }
        else if (IsInteger(type.SpecialType))
        {
            schema = "{\"type\":\"integer\"}";
        }
        else if (IsNumber(type.SpecialType))
        {
            schema = "{\"type\":\"number\"}";
        }
        else if (string.Equals(type.ToDisplayString(), "System.TimeSpan", StringComparison.Ordinal))
        {
            schema = "{\"type\":\"string\",\"format\":\"duration\"}";
        }
        else if (type is IArrayTypeSymbol array)
        {
            schema = "{\"type\":\"array\",\"items\":" +
                     BuildTypeSchema(array.ElementType, visitedTypes) +
                     "}";
        }
        else if (type.TypeKind == TypeKind.Enum && type is INamedTypeSymbol enumType)
        {
            var names = enumType.GetMembers()
                .OfType<IFieldSymbol>()
                .Where(field => field.HasConstantValue)
                .Select(field => ToCamelCase(field.Name))
                .OrderBy(name => name, StringComparer.Ordinal);
            schema = "{\"type\":\"string\",\"enum\":[" +
                     string.Join(",", names.Select(name => "\"" + EscapeJson(name) + "\"")) +
                     "]}";
        }
        else if (type is INamedTypeSymbol namedType)
        {
            schema = BuildObjectSchema(namedType, visitedTypes);
        }
        else
        {
            schema = "{}";
        }

        var range = FindAttribute(property, RangeAttributeName);
        if (range is not null &&
            (IsInteger(type.SpecialType) || IsNumber(type.SpecialType)) &&
            TryReadJsonNumber(range, 0, out var minimum) &&
            TryReadJsonNumber(range, 1, out var maximum))
        {
            schema = schema.Substring(0, schema.Length - 1) +
                     ",\"minimum\":" + minimum +
                     ",\"maximum\":" + maximum +
                     "}";
        }

        if (FindAttribute(property, SecretAttributeName) is not null)
        {
            schema = schema.Substring(0, schema.Length - 1) +
                     ",\"x-opencowork-secret\":true}";
        }

        return schema;
    }

    private static string BuildTypeSchema(
        ITypeSymbol type,
        HashSet<string> visitedTypes)
    {
        var unwrapped = UnwrapNullable(type);

        if (unwrapped.SpecialType == SpecialType.System_String)
        {
            return "{\"type\":\"string\"}";
        }

        if (unwrapped.SpecialType == SpecialType.System_Boolean)
        {
            return "{\"type\":\"boolean\"}";
        }

        if (IsInteger(unwrapped.SpecialType))
        {
            return "{\"type\":\"integer\"}";
        }

        if (IsNumber(unwrapped.SpecialType))
        {
            return "{\"type\":\"number\"}";
        }

        return unwrapped is INamedTypeSymbol namedType
            ? BuildObjectSchema(namedType, visitedTypes)
            : "{}";
    }

    private static ITypeSymbol UnwrapNullable(ITypeSymbol type)
    {
        return type is INamedTypeSymbol namedType &&
               namedType.IsGenericType &&
               namedType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
            ? namedType.TypeArguments[0]
            : type;
    }

    private static bool IsInteger(SpecialType type)
    {
        return type == SpecialType.System_Byte ||
               type == SpecialType.System_SByte ||
               type == SpecialType.System_Int16 ||
               type == SpecialType.System_UInt16 ||
               type == SpecialType.System_Int32 ||
               type == SpecialType.System_UInt32 ||
               type == SpecialType.System_Int64 ||
               type == SpecialType.System_UInt64;
    }

    private static bool IsNumber(SpecialType type)
    {
        return type == SpecialType.System_Single ||
               type == SpecialType.System_Double ||
               type == SpecialType.System_Decimal;
    }

    private static string GetTypeName(INamedTypeSymbol type)
    {
        return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    private static string ToLiteral(string value)
    {
        return SymbolDisplay.FormatLiteral(value, quote: true);
    }

    private static string ToCamelCase(string value)
    {
        if (value.Length == 0 || char.IsLower(value[0]))
        {
            return value;
        }

        return char.ToLowerInvariant(value[0]) + value.Substring(1);
    }

    private static string EscapeJson(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("\t", "\\t");
    }

    private static string NormalizeNewlines(string value)
    {
        return value.Replace("\r\n", "\n");
    }

    private sealed class Declarations
    {
        public Declarations(
            ImmutableArray<ModuleModel> modules,
            ImmutableArray<ConfigSectionModel> configSections,
            ImmutableArray<WireMethodModel> wireMethods,
            ImmutableArray<InvalidDeclarationModel> invalidDeclarations)
        {
            Modules = modules;
            ConfigSections = configSections;
            WireMethods = wireMethods;
            InvalidDeclarations = invalidDeclarations;
        }

        public ImmutableArray<ModuleModel> Modules { get; }

        public ImmutableArray<ConfigSectionModel> ConfigSections { get; }

        public ImmutableArray<WireMethodModel> WireMethods { get; }

        public ImmutableArray<InvalidDeclarationModel> InvalidDeclarations { get; }
    }

    private sealed class ModuleModel
    {
        public ModuleModel(
            INamedTypeSymbol type,
            string id,
            ImmutableArray<string> dependencies,
            int priority,
            bool canBePrimaryHost)
        {
            Type = type;
            Id = id;
            Dependencies = dependencies;
            Priority = priority;
            CanBePrimaryHost = canBePrimaryHost;
        }

        public INamedTypeSymbol Type { get; }

        public string Id { get; }

        public ImmutableArray<string> Dependencies { get; }

        public int Priority { get; }

        public bool CanBePrimaryHost { get; }
    }

    private sealed class ConfigSectionModel
    {
        public ConfigSectionModel(INamedTypeSymbol type, string name)
        {
            Type = type;
            Name = name;
        }

        public INamedTypeSymbol Type { get; }

        public string Name { get; }
    }

    private sealed class WireMethodModel
    {
        public WireMethodModel(IMethodSymbol symbol, string method)
        {
            Symbol = symbol;
            Method = method;
        }

        public IMethodSymbol Symbol { get; }

        public string Method { get; }
    }

    private sealed class InvalidDeclarationModel
    {
        public InvalidDeclarationModel(ISymbol symbol, string reason)
        {
            Symbol = symbol;
            Reason = reason;
        }

        public ISymbol Symbol { get; }

        public string Reason { get; }
    }

    private sealed class PendingDiagnostic
    {
        public PendingDiagnostic(
            DiagnosticDescriptor descriptor,
            ISymbol symbol,
            string sortKey,
            params object[] arguments)
        {
            Descriptor = descriptor;
            Symbol = symbol;
            SortKey = sortKey;
            Arguments = arguments;
        }

        public DiagnosticDescriptor Descriptor { get; }

        public ISymbol Symbol { get; }

        public string SortKey { get; }

        public object[] Arguments { get; }
    }
}
