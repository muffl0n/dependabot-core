using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Versioning;

using NuGetUpdater.Core.Discover;

namespace NuGetUpdater.Core.Analyze;

using MultiDependency = (string PropertyName, ImmutableArray<string> TargetFrameworks, ImmutableHashSet<string> DependencyNames);

public partial class AnalyzeWorker
{
    public const string AnalysisDirectoryName = "./.dependabot/analysis";

    private readonly Logger _logger;

    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(), new RequirementConverter() },
    };

    public AnalyzeWorker(Logger logger)
    {
        _logger = logger;
    }

    public async Task RunAsync(string repoRoot, string discoveryPath, string dependencyPath, string analysisDirectory)
    {
        var discovery = await DeserializeJsonFileAsync<WorkspaceDiscoveryResult>(discoveryPath, nameof(WorkspaceDiscoveryResult));
        var dependencyInfo = await DeserializeJsonFileAsync<DependencyInfo>(dependencyPath, nameof(DependencyInfo));
        var startingDirectory = PathHelper.JoinPath(repoRoot, discovery.Path);

        _logger.Log($"Starting analysis of {dependencyInfo.Name}...");

        // We need to find all projects which have the given dependency. Even in cases that they
        // have it transitively may require that peer dependencies be updated in the project.
        var projectsWithDependency = discovery.Projects
            .Where(p => p.Dependencies.Any(d => d.Name.Equals(dependencyInfo.Name, StringComparison.OrdinalIgnoreCase)))
            .ToImmutableArray();
        var projectFrameworks = projectsWithDependency
            .SelectMany(p => p.TargetFrameworks)
            .Distinct()
            .Select(NuGetFramework.Parse)
            .ToImmutableArray();
        var propertyBasedDependencies = discovery.Projects.SelectMany(p
            => p.Dependencies.Where(d => !d.IsTransitive &&
                d.EvaluationResult?.RootPropertyName is not null)
            ).ToImmutableArray();

        bool canUpdate = false;
        bool usesMultiDependencyProperty = false;
        NuGetVersion? updatedVersion = null;
        ImmutableArray<Dependency> updatedDependencies = [];

        bool isUpdateNecessary = IsUpdateNecessary(dependencyInfo, projectsWithDependency);
        if (isUpdateNecessary)
        {
            var nugetContext = new NuGetContext(startingDirectory);
            if (!Directory.Exists(nugetContext.TempPackageDirectory))
            {
                Directory.CreateDirectory(nugetContext.TempPackageDirectory);
            }

            _logger.Log($"  Determining multi-dependency property.");
            var multiDependencies = DetermineMultiDependencyDetails(
                discovery,
                dependencyInfo.Name,
                propertyBasedDependencies);

            usesMultiDependencyProperty = multiDependencies.Any(md => md.DependencyNames.Count > 1);
            var dependenciesToUpdate = usesMultiDependencyProperty
                ? multiDependencies
                    .SelectMany(md => md.DependencyNames)
                    .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase)
                : [dependencyInfo.Name];
            var applicableTargetFrameworks = usesMultiDependencyProperty
                ? multiDependencies
                    .SelectMany(md => md.TargetFrameworks)
                    .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase)
                    .Select(NuGetFramework.Parse)
                    .ToImmutableArray()
                : projectFrameworks;

            _logger.Log($"  Finding updated version.");
            updatedVersion = await FindUpdatedVersionAsync(
                startingDirectory,
                dependencyInfo,
                dependenciesToUpdate,
                applicableTargetFrameworks,
                nugetContext,
                _logger,
                CancellationToken.None);

            _logger.Log($"  Finding updated peer dependencies.");
            updatedDependencies = updatedVersion is not null
                ? await FindUpdatedDependenciesAsync(
                    startingDirectory,
                    discovery,
                    dependenciesToUpdate,
                    updatedVersion,
                    nugetContext,
                    _logger,
                    CancellationToken.None)
                : [];

            //TODO: At this point we should add the peer dependencies to a queue where
            // we will analyze them one by one to see if they themselves are part of a
            // multi-dependency property. Basically looping this if-body until we have
            // emptied the queue and have a complete list of updated dependencies. We
            // should track the dependenciesToUpdate as they have already been analyzed.
        }

        var result = new AnalysisResult
        {
            UpdatedVersion = updatedVersion?.ToNormalizedString() ?? dependencyInfo.Version,
            CanUpdate = updatedVersion is not null,
            VersionComesFromMultiDependencyProperty = usesMultiDependencyProperty,
            UpdatedDependencies = updatedDependencies,
        };

        await WriteResultsAsync(analysisDirectory, dependencyInfo.Name, result, _logger);

        _logger.Log($"Analysis complete.");
    }

    private static bool IsUpdateNecessary(DependencyInfo dependencyInfo, ImmutableArray<ProjectDiscoveryResult> projectsWithDependency)
    {
        if (projectsWithDependency.Length == 0)
        {
            return false;
        }

        // We will even attempt to update transitive dependencies if the dependency is vulnerable.
        if (dependencyInfo.IsVulnerable)
        {
            return true;
        }

        // Since the dependency is not vulnerable, we only need to update if it is not transitive.
        return projectsWithDependency.Any(p =>
            p.Dependencies.Any(d =>
                d.Name.Equals(dependencyInfo.Name, StringComparison.OrdinalIgnoreCase) &&
                !d.IsTransitive));
    }

    internal static async Task<T> DeserializeJsonFileAsync<T>(string path, string fileType)
    {
        var json = File.Exists(path)
            ? await File.ReadAllTextAsync(path)
            : throw new FileNotFoundException($"{fileType} file not found.", path);

        return JsonSerializer.Deserialize<T>(json, SerializerOptions)
            ?? throw new InvalidOperationException($"{fileType} file is empty.");
    }

    internal static async Task<NuGetVersion?> FindUpdatedVersionAsync(
        string startingDirectory,
        DependencyInfo dependencyInfo,
        ImmutableHashSet<string> packageIds,
        ImmutableArray<NuGetFramework> projectFrameworks,
        NuGetContext nugetContext,
        Logger logger,
        CancellationToken cancellationToken)
    {
        var versionResult = await VersionFinder.GetVersionsAsync(
            dependencyInfo,
            nugetContext,
            logger,
            cancellationToken);

        return await FindUpdatedVersionAsync(
            packageIds,
            dependencyInfo.Version,
            versionResult,
            projectFrameworks,
            findLowestVersion: dependencyInfo.IsVulnerable,
            nugetContext,
            logger,
            cancellationToken);
    }

    internal static async Task<NuGetVersion?> FindUpdatedVersionAsync(
        ImmutableHashSet<string> packageIds,
        ImmutableArray<NuGetFramework> projectFrameworks,
        NuGetVersion version,
        bool findLowestVersion,
        NuGetContext nugetContext,
        Logger logger,
        CancellationToken cancellationToken)
    {
        var versionResult = await VersionFinder.GetVersionsAsync(
            packageIds.First(),
            version,
            nugetContext,
            logger,
            cancellationToken);

        return await FindUpdatedVersionAsync(
            packageIds,
            version.ToNormalizedString(),
            versionResult,
            projectFrameworks,
            findLowestVersion,
            nugetContext,
            logger,
            cancellationToken);
    }

    internal static async Task<NuGetVersion?> FindUpdatedVersionAsync(
        ImmutableHashSet<string> packageIds,
        string versionString,
        VersionResult versionResult,
        ImmutableArray<NuGetFramework> projectFrameworks,
        bool findLowestVersion,
        NuGetContext nugetContext,
        Logger logger,
        CancellationToken cancellationToken)
    {
        var versions = versionResult.GetVersions();
        var orderedVersions = findLowestVersion
            ? versions.OrderBy(v => v) // If we are fixing a vulnerability, then we want the lowest version that is safe.
            : versions.OrderByDescending(v => v); // If we are just updating versions, then we want the highest version possible.

        return await FindFirstCompatibleVersion(
            packageIds,
            versionString,
            versionResult,
            orderedVersions,
            projectFrameworks,
            nugetContext,
            logger,
            cancellationToken);
    }

    internal static async Task<NuGetVersion?> FindFirstCompatibleVersion(
        ImmutableHashSet<string> packageIds,
        string versionString,
        VersionResult versionResult,
        IEnumerable<NuGetVersion> orderedVersions,
        ImmutableArray<NuGetFramework> projectFrameworks,
        NuGetContext nugetContext,
        Logger logger,
        CancellationToken cancellationToken)
    {
        if (NuGetVersion.TryParse(versionString, out var currentVersion))
        {
            var isCompatible = await AreAllPackagesCompatibleAsync(
                packageIds,
                currentVersion,
                projectFrameworks,
                nugetContext,
                logger,
                cancellationToken);

            if (!isCompatible)
            {
                // If the current package is incompatible, then don't check for compatibility.
                return orderedVersions.First();
            }
        }

        foreach (var version in orderedVersions)
        {
            var existsForAll = await VersionFinder.DoVersionsExistAsync(packageIds, version, nugetContext, logger, cancellationToken);
            if (!existsForAll)
            {
                continue;
            }

            var isCompatible = await AreAllPackagesCompatibleAsync(
                packageIds,
                version,
                projectFrameworks,
                nugetContext,
                logger,
                cancellationToken);

            if (isCompatible)
            {
                return version;
            }
        }

        // Could not find a compatible version
        return null;
    }

    internal static async Task<bool> AreAllPackagesCompatibleAsync(
        ImmutableHashSet<string> packageIds,
        NuGetVersion currentVersion,
        ImmutableArray<NuGetFramework> projectFrameworks,
        NuGetContext nugetContext,
        Logger logger,
        CancellationToken cancellationToken)
    {
        foreach (var packageId in packageIds)
        {
            var isCompatible = await CompatibilityChecker.CheckAsync(
                new(packageId, currentVersion),
                projectFrameworks,
                nugetContext,
                logger,
                cancellationToken);
            if (!isCompatible)
            {
                return false;
            }
        }

        return true;
    }

    internal static async Task<ImmutableDictionary<NuGetFramework, ImmutableArray<Dependency>>> GetDependenciesAsync(
        string workspacePath,
        string projectPath,
        IEnumerable<NuGetFramework> frameworks,
        Dependency package,
        Logger logger)
    {
        var result = ImmutableDictionary.CreateBuilder<NuGetFramework, ImmutableArray<Dependency>>();
        foreach (var framework in frameworks)
        {
            var dependencies = await MSBuildHelper.GetAllPackageDependenciesAsync(
                workspacePath,
                projectPath,
                framework.ToString(),
                [package],
                logger);
            result.Add(framework, [.. dependencies]);
        }
        return result.ToImmutable();
    }

    internal static async Task<ImmutableArray<Dependency>> FindUpdatedDependenciesAsync(
        string repoRoot,
        WorkspaceDiscoveryResult discovery,
        ImmutableHashSet<string> packageIds,
        NuGetVersion updatedVersion,
        NuGetContext nugetContext,
        Logger logger,
        CancellationToken cancellationToken)
    {
        // We need to find all projects which have the given dependency. Even in cases that they
        // have it transitively may require that peer dependencies be updated in the project.
        var projectsWithDependency = discovery.Projects
            .Where(p => p.Dependencies.Any(d => packageIds.Contains(d.Name)))
            .ToImmutableArray();
        if (projectsWithDependency.Length == 0)
        {
            return [];
        }

        var projectFrameworks = projectsWithDependency
            .SelectMany(p => p.TargetFrameworks)
            .Distinct()
            .Select(NuGetFramework.Parse)
            .ToImmutableArray();

        // When updating peer dependencies, we only need to consider top-level dependencies.
        var projectDependencyNames = projectsWithDependency
            .SelectMany(p => p.Dependencies)
            .Where(d => !d.IsTransitive)
            .Select(d => d.Name)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

        // Determine updated peer dependencies
        var workspacePath = PathHelper.JoinPath(repoRoot, discovery.Path);
        // We need any project path so the dependency finder can locate the nuget.config
        var projectPath = Path.Combine(workspacePath, projectsWithDependency.First().FilePath);

        // Create distinct list of dependencies taking the highest version of each
        var dependencyResult = await DependencyFinder.GetDependenciesAsync(
            workspacePath,
            projectPath,
            projectFrameworks,
            packageIds,
            updatedVersion,
            logger);

        // Filter dependencies by whether any project references them
        return dependencyResult.GetDependencies()
            .Where(d => projectDependencyNames.Contains(d.Name))
            .ToImmutableArray();
    }

    internal static ImmutableArray<MultiDependency> DetermineMultiDependencyDetails(
        WorkspaceDiscoveryResult discovery,
        string packageId,
        ImmutableArray<Dependency> propertyBasedDependencies)
    {
        var packageDeclarationsUsingProperty = discovery.Projects
            .SelectMany(p =>
                p.Dependencies.Where(d => !d.IsTransitive &&
                    d.Name.Equals(packageId, StringComparison.OrdinalIgnoreCase) &&
                    d.EvaluationResult?.RootPropertyName is not null)
            ).ToImmutableArray();

        return packageDeclarationsUsingProperty
            .Select(d => d.EvaluationResult!.RootPropertyName!)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase)
            .Select(property =>
            {
                // Find all dependencies that use the same property
                var packages = propertyBasedDependencies
                    .Where(d => property.Equals(d.EvaluationResult?.RootPropertyName, StringComparison.OrdinalIgnoreCase));

                // Combine all their target frameworks
                var tfms = packages.SelectMany(d => d.TargetFrameworks ?? [])
                    .Distinct()
                    .ToImmutableArray();

                var packageIds = packages.Select(d => d.Name)
                    .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

                return (property, tfms, packageIds);
            }).ToImmutableArray();
    }

    internal static async Task WriteResultsAsync(string analysisDirectory, string dependencyName, AnalysisResult result, Logger logger)
    {
        if (!Directory.Exists(analysisDirectory))
        {
            Directory.CreateDirectory(analysisDirectory);
        }

        var resultPath = Path.Combine(analysisDirectory, $"{dependencyName}.json");

        logger.Log($"  Writing analysis result to [{resultPath}].");

        var resultJson = JsonSerializer.Serialize(result, SerializerOptions);
        await File.WriteAllTextAsync(path: resultPath, resultJson);
    }
}
