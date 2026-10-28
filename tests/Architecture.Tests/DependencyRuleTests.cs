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
    private const string Ai = "PortfolioIntelligence.Ai";
    private const string AiGemini = "PortfolioIntelligence.Ai.Gemini";
    private const string Researcher = "PortfolioIntelligence.Researcher";
    private const string AiCli = "PortfolioIntelligence.AiCli";
    private const string SourceGateway = "PortfolioIntelligence.SourceGateway";
    private const string SourcesCli = "PortfolioIntelligence.SourcesCli";
    private const string GeminiSdk = "Google.GenAI";
    private static readonly string[] TextPackages = ["PdfPig", "AngleSharp"];

    private static readonly ProjectGraph Graph = ProjectGraph.Load(Path.Combine(RepositoryRoot.Path, "src"));

    [Fact]
    public void Graph_contains_the_expected_projects()
    {
        Assert.Superset(
            new HashSet<string> { Kernel, Research, ResearchHost, Execution, TradingHost, MarketData, HoldoutEvaluator, Ai, AiGemini, Researcher, AiCli, SourceGateway, SourcesCli },
            Graph.Projects.ToHashSet());
    }

    [Theory]
    [InlineData(Research)]
    [InlineData(ResearchHost)]
    [InlineData(Researcher)]
    [InlineData(Ai)]
    [InlineData(SourceGateway)]
    [InlineData(SourcesCli)]
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
    [InlineData(Ai)]
    [InlineData(AiGemini)]
    [InlineData(Researcher)]
    [InlineData(AiCli)]
    [InlineData(SourceGateway)]
    [InlineData(SourcesCli)]
    public void Nothing_outside_the_evaluator_references_the_cohort_registry(string project)
    {
        // Research reaches the evaluator only through its narrow API; risk and execution never see cohorts (testing §7.3).
        Assert.DoesNotContain(HoldoutEvaluator, Graph.TransitiveReferencesOf(project));
    }

    [Theory]
    [InlineData(Execution)]
    [InlineData(TradingHost)]
    public void Trading_reaches_only_the_kernel_market_data_and_infrastructure(string project)
    {
        // ADR-0018 §4, testing §7.3: risk and execution never see research, AI, the evaluator or cohort code.
        var references = Graph.TransitiveReferencesOf(project);
        foreach (var forbidden in new[] { Research, ResearchHost, Researcher, Ai, AiGemini, HoldoutEvaluator })
        {
            Assert.DoesNotContain(forbidden, references);
        }

        Assert.Equal(new[] { Kernel, MarketData }, Graph.TransitiveReferencesOf(Execution).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Environment_probe_and_backup_tool_reference_no_project()
    {
        Assert.Empty(Graph.DirectReferencesOf("PortfolioIntelligence.HoldoutEvaluator.EnvironmentProbe"));
        Assert.Empty(Graph.DirectReferencesOf("PortfolioIntelligence.BackupCli"));
    }

    [Fact]
    public void Ai_module_depends_only_on_the_kernel()
    {
        // ADR-0017 §1: provider-neutral; never research, execution, a host or an adapter.
        Assert.Equal(new[] { Kernel }, Graph.TransitiveReferencesOf(Ai));
        Assert.DoesNotContain(GeminiSdk, Graph.PackagesOf(Ai));
    }

    [Fact]
    public void Gemini_adapter_depends_only_on_the_ai_module_and_the_kernel()
    {
        Assert.Equal(new[] { Ai, Kernel }, Graph.TransitiveReferencesOf(AiGemini).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Gemini_sdk_is_referenced_only_by_the_adapter()
    {
        foreach (var project in Graph.Projects.Where(p => p != AiGemini))
        {
            Assert.DoesNotContain(GeminiSdk, Graph.PackagesOf(project));
        }

        Assert.Contains(GeminiSdk, Graph.PackagesOf(AiGemini));
    }

    [Fact]
    public void Researcher_reaches_only_the_kernel_and_the_ai_module_and_takes_no_database_or_sdk_package()
    {
        // Roadmap M4 contract: no holdout, evaluator, SQL, shell or web access; the outside is IResearchToolBackend.
        Assert.Equal(new[] { Ai, Kernel }, Graph.TransitiveReferencesOf(Researcher).Order(StringComparer.Ordinal));
        Assert.Empty(Graph.PackagesOf(Researcher));
        Assert.DoesNotContain(AiGemini, Graph.TransitiveReferencesOf(Research));
    }

    [Fact]
    public void Source_gateway_depends_only_on_the_kernel_and_is_never_reached_by_the_researcher_or_trading()
    {
        // ADR-0022 §1: the only web egress; no AI, research, execution, evaluator or host code. The Researcher sees findings
        // only through its tool port, and trading never reaches web content.
        Assert.Equal(new[] { Kernel }, Graph.TransitiveReferencesOf(SourceGateway));
        Assert.DoesNotContain(SourceGateway, Graph.TransitiveReferencesOf(Researcher));
        Assert.DoesNotContain(SourceGateway, Graph.TransitiveReferencesOf(Execution));
        Assert.DoesNotContain(SourceGateway, Graph.TransitiveReferencesOf(TradingHost));
        Assert.DoesNotContain(SourceGateway, Graph.TransitiveReferencesOf(Ai));
    }

    [Fact]
    public void Pdf_and_html_packages_are_referenced_only_by_the_source_gateway()
    {
        foreach (var project in Graph.Projects.Where(p => p != SourceGateway))
        {
            foreach (var package in TextPackages)
            {
                Assert.DoesNotContain(package, Graph.PackagesOf(project));
            }
        }

        Assert.All(TextPackages, p => Assert.Contains(p, Graph.PackagesOf(SourceGateway)));
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
