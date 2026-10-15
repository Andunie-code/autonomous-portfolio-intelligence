namespace PortfolioIntelligence.Architecture.Tests;

/// <summary>Module boundaries from ADR-0001 and architecture.md §1.</summary>
public class DependencyRuleTests
{
    private const string Kernel = "PortfolioIntelligence.Kernel";
    private const string Research = "PortfolioIntelligence.Research";
    private const string ResearchHost = "PortfolioIntelligence.Research.Host";
    private const string Execution = "PortfolioIntelligence.Execution";
    private const string TradingHost = "PortfolioIntelligence.Trading.Host";
    private const string MarketData = "PortfolioIntelligence.MarketData";
    private const string HoldoutEvaluator = "PortfolioIntelligence.HoldoutEvaluator";
    private const string Infrastructure = "PortfolioIntelligence.Infrastructure";

    private static readonly ProjectGraph Graph = ProjectGraph.Load(Path.Combine(RepositoryRoot.Path, "src"));

    [Fact]
    public void Graph_contains_the_expected_projects()
    {
        Assert.Superset(
            new HashSet<string> { Kernel, Research, ResearchHost, Execution, TradingHost, MarketData, HoldoutEvaluator },
            Graph.Projects.ToHashSet());
    }

    [Theory]
    [InlineData(Research)]
    [InlineData(ResearchHost)]
    public void Research_side_never_reaches_execution_or_trading_host(string project)
    {
        var references = Graph.TransitiveReferencesOf(project);

        Assert.DoesNotContain(Execution, references);
        Assert.DoesNotContain(TradingHost, references);
    }

    [Fact]
    public void MarketData_depends_only_on_the_kernel()
    {
        // M1 data access must not pull in execution, research or host code (YAGNI, T23).
        Assert.Equal(new[] { Kernel }, Graph.TransitiveReferencesOf(MarketData));
    }

    [Fact]
    public void Execution_does_not_depend_on_research()
    {
        Assert.DoesNotContain(Research, Graph.TransitiveReferencesOf(Execution));
    }

    [Fact]
    public void Holdout_evaluator_depends_only_on_the_kernel_and_infrastructure()
    {
        // ADR-0009/ADR-0013: the evaluator shares the kernel (parity) but no research, execution, market-data or host code.
        Assert.Equal(new[] { Infrastructure, Kernel }, Graph.TransitiveReferencesOf(HoldoutEvaluator).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(Research)]
    [InlineData(ResearchHost)]
    [InlineData(Execution)]
    [InlineData(TradingHost)]
    [InlineData(MarketData)]
    public void Nothing_outside_the_evaluator_references_the_cohort_registry(string project)
    {
        // Research reaches the evaluator only through its narrow API; risk and execution never see cohorts (testing §7.3).
        Assert.DoesNotContain(HoldoutEvaluator, Graph.TransitiveReferencesOf(project));
    }

    [Fact]
    public void Kernel_has_no_project_or_package_dependencies()
    {
        Assert.Empty(Graph.DirectReferencesOf(Kernel));
        Assert.Empty(Graph.PackagesOf(Kernel));
    }

    [Fact]
    public void No_project_references_a_host()
    {
        foreach (var project in Graph.Projects)
        {
            var references = Graph.DirectReferencesOf(project);
            Assert.DoesNotContain(references, r => r.EndsWith(".Host", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Transitive_check_detects_an_indirect_forbidden_reference()
    {
        // Guard against a vacuous rule: in a fake graph A -> B -> C, A must be seen to reach C.
        var root = Directory.CreateTempSubdirectory("pi-graph-");
        try
        {
            WriteProject(root.FullName, "A", "B");
            WriteProject(root.FullName, "B", "C");
            WriteProject(root.FullName, "C");

            Assert.Contains("C", ProjectGraph.Load(root.FullName).TransitiveReferencesOf("A"));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static void WriteProject(string root, string name, params string[] references)
    {
        var items = string.Concat(references.Select(r => $"<ProjectReference Include=\"..\\{r}\\{r}.csproj\" />"));
        var directory = Directory.CreateDirectory(Path.Combine(root, name));
        File.WriteAllText(Path.Combine(directory.FullName, $"{name}.csproj"), $"<Project><ItemGroup>{items}</ItemGroup></Project>");
    }
}
