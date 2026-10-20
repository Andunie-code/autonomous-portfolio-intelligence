using PortfolioIntelligence.Execution;
using PortfolioIntelligence.Kernel.Backtest;

namespace PortfolioIntelligence.Trading.Tests;

/// <summary>RiskConfig documents with the P1 cluster cap (U, 2026-10-20): compatibility, mapping and loosening.</summary>
public class RiskConfigDocumentTests
{
    private static readonly RiskConfigDocument WithoutCap = RiskConfigDocument.From(RiskConfig.Policy);
    private static readonly RiskConfigDocument WithCap = RiskConfigDocument.From(RiskConfig.Policy with { ClusterCap = RiskConfig.P1ClusterCap }, RiskConfig.P1ClusterWarning);

    [Fact]
    public void Documents_written_before_P1_keep_their_exact_json_and_hash()
    {
        const string Stored = """{"maxOpenPositions":5,"maxAssetWeight":0.20,"maxTotalExposure":0.80,"dailyLossLimit":0.03,"maxDrawdown":0.15,"drawdownHaltEnabled":true,"maxPositionLoss":0.10,"experimentalPairsMaxExposure":0.10,"maxDataLagBars":2}""";
        var parsed = System.Text.Json.JsonSerializer.Deserialize<RiskConfigDocument>(Stored, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase })!;

        Assert.Equal(Stored, parsed.Json());
        Assert.Equal(WithoutCap.Json(), parsed.Json());
        Assert.Null(parsed.ToConfig().ClusterCap);
        Assert.DoesNotContain("cluster", WithoutCap.Json(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_P1_cap_maps_to_the_engine_and_new_runs_start_with_it()
    {
        Assert.Contains("\"clusterCapThreshold\":0.7", WithCap.Json());
        Assert.Contains("\"clusterCapMaxExposure\":0.60", WithCap.Json());
        Assert.Contains("\"clusterWarningShare\":0.54", WithCap.Json());
        Assert.Equal(RiskConfig.P1ClusterCap, WithCap.ToConfig().ClusterCap);
        Assert.Empty(WithCap.Errors());
        Assert.NotEmpty((WithCap with { ClusterCapThreshold = null }).Errors());

        var manifest = CohortManifestReader.Read(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "cohort-0.json")));
        Assert.Equal(RiskConfig.P1ClusterCap, PaperTrader.InitialRisk(manifest).ToConfig().ClusterCap);
        Assert.Equal(RiskConfig.P1ClusterWarning, PaperTrader.InitialRisk(manifest).ClusterWarningShare);
    }

    [Fact]
    public void Adding_the_cap_tightens_and_removing_or_raising_it_loosens()
    {
        Assert.False(WithoutCap.IsLoosenedBy(WithCap));
        Assert.True(WithCap.IsLoosenedBy(WithoutCap));
        Assert.True(WithCap.IsLoosenedBy(WithCap with { ClusterCapMaxExposure = 0.70m }));
        Assert.True(WithCap.IsLoosenedBy(WithCap with { ClusterCapThreshold = 0.80m }));
        Assert.False(WithCap.IsLoosenedBy(WithCap with { ClusterCapMaxExposure = 0.50m }));
    }
}
