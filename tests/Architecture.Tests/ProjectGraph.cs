using System.Xml.Linq;

namespace PortfolioIntelligence.Architecture.Tests;

/// <summary>
/// Project dependency graph read from the .csproj files themselves. Checking ProjectReference
/// (rather than compiled assembly references) catches a forbidden dependency even when no type
/// from it is used yet, because the compiler drops unused assembly references.
/// </summary>
internal sealed class ProjectGraph
{
    private readonly Dictionary<string, HashSet<string>> _directReferences;
    private readonly Dictionary<string, HashSet<string>> _packageReferences;

    private ProjectGraph(Dictionary<string, HashSet<string>> directReferences, Dictionary<string, HashSet<string>> packageReferences)
    {
        _directReferences = directReferences;
        _packageReferences = packageReferences;
    }

    public IReadOnlyCollection<string> Projects => _directReferences.Keys;

    public static ProjectGraph Load(string sourceRoot)
    {
        var direct = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var packages = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*.csproj", SearchOption.AllDirectories))
        {
            // Load from a stream: XDocument.Load(string) treats the argument as a URI and fails on some valid paths.
            using var stream = File.OpenRead(file);
            var document = XDocument.Load(stream);
            var name = System.IO.Path.GetFileNameWithoutExtension(file);
            direct[name] = document.Descendants("ProjectReference")
                .Select(e => System.IO.Path.GetFileNameWithoutExtension(e.Attribute("Include")!.Value))
                .ToHashSet(StringComparer.Ordinal);
            packages[name] = document.Descendants("PackageReference")
                .Select(e => e.Attribute("Include")!.Value)
                .ToHashSet(StringComparer.Ordinal);
        }

        return new ProjectGraph(direct, packages);
    }

    public IReadOnlySet<string> PackagesOf(string project) => _packageReferences[project];

    public IReadOnlySet<string> DirectReferencesOf(string project) => _directReferences[project];

    public IReadOnlySet<string> TransitiveReferencesOf(string project)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(_directReferences[project]);
        while (pending.Count > 0)
        {
            var next = pending.Pop();
            if (visited.Add(next) && _directReferences.TryGetValue(next, out var children))
            {
                foreach (var child in children)
                {
                    pending.Push(child);
                }
            }
        }

        return visited;
    }
}
