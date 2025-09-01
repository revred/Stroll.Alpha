using Stroll.Alpha.Dataset.Config;
using Stroll.Alpha.Dataset.Sources;
using Stroll.Alpha.Dataset.Storage;
using Stroll.Alpha.Market.Services;
using Xunit;

public class DatasetTests
{
    [Fact]
    public async Task CompletenessReport_IsComputed()
    {
        var cfg = DatasetConfig.Default;
        var src = new ThetaDbSource();
        var store = new DatasetStore(src, src, cfg);
        var rep = await store.CheckChainCompletenessAsync("SPX", DateTime.UtcNow);
        Assert.NotNull(rep);
        Assert.True(rep.Score >= 0);
    }
}

public class MarketApiTests
{
    [Fact]
    public async Task ChainSummary_Works()
    {
        var cfg = DatasetConfig.Default;
        var src = new ThetaDbSource();
        var svc = new MarketQueryService(cfg, src, src);
        var res = await svc.GetChainAsync(new Stroll.Alpha.Market.Contracts.ChainRequest("SPX", DateTime.UtcNow), default);
        Assert.True(res.Completeness >= 0);
    }
}

public class RiskPolicyTests
{
    [Fact]
    public void LossCapPolicy_EnforcesSteps()
    {
        var p = new Stroll.Alpha.Market.Risk.LossCapPolicy();
        Assert.Equal(500m, p.GetCap(0).cap);
        Assert.Equal(300m, p.GetCap(1).cap);
        Assert.Equal(200m, p.GetCap(2).cap);
        Assert.Equal(100m, p.GetCap(3).cap);
    }
}

public class ProbeTests
{
    [Fact]
    public async Task Probes_WarnOnLowStrikes()
    {
        var cfg = DatasetConfig.Default;
        var src = new ThetaDbSource();
        var store = new DatasetStore(src, src, cfg);
        var rep = await store.CheckChainCompletenessAsync("SPX", DateTime.UtcNow);
        // stub source returns only 2 per side, so expect warning
        Assert.Contains("insufficient_put_strikes", rep.Warnings);
    }
}
