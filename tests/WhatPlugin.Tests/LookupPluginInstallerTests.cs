using WhatPlugin.Core;

namespace WhatPlugin.Tests;

/// <summary>Mac 版把查询插件装到 VST3 文件夹。源和目标都是临时目录，每次先确认路径在临时目录里。</summary>
public sealed class LookupPluginInstallerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pv-lookup-" + Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly string _target;

    public LookupPluginInstallerTests()
    {
        _source = Path.Combine(_dir, "App", "Resources", LookupPluginInstaller.BundleName);
        _target = Path.Combine(_dir, "VST3", LookupPluginInstaller.BundleName);
        Assert.StartsWith(Path.GetTempPath(), _source, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Path.GetTempPath(), _target, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    /// <summary>假的插件包：程序文件和 Info.plist。</summary>
    private void MakeBundle(string bundle, string binary)
    {
        Assert.StartsWith(_dir, bundle, StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(Path.Combine(bundle, "Contents", "MacOS"));
        File.WriteAllText(Path.Combine(bundle, "Contents", "MacOS", "WhatPluginLookup"), binary);
        File.WriteAllText(Path.Combine(bundle, "Contents", "Info.plist"), "plist");
    }

    [Fact]
    public void WithoutBundledPluginNothingIsOffered()
    {
        var installer = new LookupPluginInstaller(_source, _target);
        Assert.Equal(LookupPluginState.NotBundled, installer.State);
        Assert.Equal("", installer.StateText);
        Assert.Equal("这个版本没有带查询插件", installer.Install());
        Assert.False(Directory.Exists(_target));
    }

    [Fact]
    public void InstallsCopyAndRecognisesIt()
    {
        MakeBundle(_source, "v1");
        var installer = new LookupPluginInstaller(_source, _target);
        Assert.Equal(LookupPluginState.NotInstalled, installer.State);

        Assert.Null(installer.Install());
        Assert.Equal(LookupPluginState.Installed, installer.State);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(_target, "Contents", "MacOS", "WhatPluginLookup")));
        Assert.Contains(_target, installer.StateText);
        // 源原样留着，VST3 文件夹里没有留下临时文件夹。
        Assert.True(Directory.Exists(_source));
        Assert.Equal([_target], Directory.GetFileSystemEntries(Path.GetDirectoryName(_target)!));
    }

    [Fact]
    public void NewerBundleReplacesOldCopy()
    {
        MakeBundle(_source, "v2");
        MakeBundle(_target, "v1");
        File.WriteAllText(Path.Combine(_target, "Contents", "old-only.txt"), "旧版本才有的文件");
        var installer = new LookupPluginInstaller(_source, _target);
        Assert.Equal(LookupPluginState.Outdated, installer.State);

        Assert.Null(installer.Install());
        Assert.Equal(LookupPluginState.Installed, installer.State);
        Assert.Equal("v2", File.ReadAllText(Path.Combine(_target, "Contents", "MacOS", "WhatPluginLookup")));
        Assert.False(File.Exists(Path.Combine(_target, "Contents", "old-only.txt")));
        Assert.Single(Directory.GetFileSystemEntries(Path.GetDirectoryName(_target)!));
    }

    [Fact]
    public void RenamedFileCountsAsDifferentVersion()
    {
        MakeBundle(_source, "v1");
        Assert.Null(new LookupPluginInstaller(_source, _target).Install());
        File.Move(Path.Combine(_target, "Contents", "Info.plist"), Path.Combine(_target, "Contents", "Other.plist"));
        Assert.Equal(LookupPluginState.Outdated, new LookupPluginInstaller(_source, _target).State);
    }

    [Fact]
    public void RefusesToInstallUnderAnotherName()
    {
        MakeBundle(_source, "v1");
        var other = Path.Combine(_dir, "VST3", "Someone Else.vst3");
        MakeBundle(other, "别人的插件");
        Assert.Equal("安装位置不对", new LookupPluginInstaller(_source, other).Install());
        Assert.Equal("别人的插件", File.ReadAllText(Path.Combine(other, "Contents", "MacOS", "WhatPluginLookup")));
    }

    [Fact]
    public void ThisComputerTargetsUserVst3Folder()
    {
        var installer = LookupPluginInstaller.ThisComputer();
        Assert.EndsWith(Path.Combine("Library", "Audio", "Plug-Ins", "VST3", LookupPluginInstaller.BundleName), installer.Target);
        Assert.EndsWith(Path.Combine("Resources", LookupPluginInstaller.BundleName), installer.Source);
        // 只看路径，不装：测试不碰真实的 VST3 文件夹。
    }
}
