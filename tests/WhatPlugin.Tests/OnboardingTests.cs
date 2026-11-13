using WhatPlugin.Core;

namespace WhatPlugin.Tests;

public sealed class OnboardingTests
{
    [Fact]
    public void ShowsOnlyForNewUsers()
    {
        Assert.Equal(OnboardingStart.Show, Onboarding.OnStartup(null, 0));
        Assert.Equal(OnboardingStart.Show, Onboarding.OnStartup("", 0));
        Assert.Equal(OnboardingStart.Show, Onboarding.OnStartup("坏值", 0));
        // 老用户升级：已经有插件，直接记成看过，不弹。
        Assert.Equal(OnboardingStart.MarkSeen, Onboarding.OnStartup(null, 12));
        Assert.Equal(OnboardingStart.Nothing, Onboarding.OnStartup(Onboarding.CurrentVersion.ToString(), 0));
        Assert.Equal(OnboardingStart.Nothing, Onboarding.OnStartup((Onboarding.CurrentVersion + 1).ToString(), 0));
        Assert.Equal(OnboardingStart.Show, Onboarding.OnStartup((Onboarding.CurrentVersion - 1).ToString(), 0));
    }

    [Fact]
    public void StopsFollowCustomNavOrder()
    {
        var stops = Onboarding.Stops(["Chains", "Plugins", "ext:成就", "Blacklist", "Settings"]);
        Assert.Equal(["Chains", "Plugins", "ext:成就", "Blacklist", "Settings"], stops.Select(s => s.Target));
        Assert.Equal("混音笔记", stops[0].Title);
        Assert.Contains("不联网", stops[2].Body);
    }

    [Fact]
    public void EveryBuiltInPageHasAStop()
    {
        string[] pages = ["Plugins", "Projects", "Chains", "Idle", "Health", "Wishes", "Accounts", "Blacklist", "Settings"];
        var stops = Onboarding.Stops(pages);
        Assert.Equal(pages.Length, stops.Count);
        Assert.All(stops, s => Assert.False(string.IsNullOrWhiteSpace(s.Body)));
    }

    [Fact]
    public void UnknownExtensionGetsGenericStopAndJunkIsSkipped()
    {
        var stops = Onboarding.Stops(["ext:统计", "ext:", "Nope", "Plugins", "Plugins"]);
        Assert.Equal(["ext:统计", "Plugins"], stops.Select(s => s.Target));
        Assert.Equal("统计", stops[0].Title);
        Assert.Equal("3 / 11", Onboarding.Progress(2, 11));
    }
}
