using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace WhatPlugin.Core;

public enum LoadOutcome { Success, NoPlugins, Failed, TimedOut }

public sealed record LoadResult(LoadOutcome Outcome, IReadOnlyList<PluginClassInfo> Classes, string? Error);

/// <summary>为每个文件启动一个扫描器进程，隔离插件的崩溃和卡死。</summary>
public sealed class ScannerRunner(string scannerPath, TimeSpan timeout)
{
    public string ScannerPath { get; } = scannerPath;
    public TimeSpan Timeout { get; } = timeout;

    public async Task<LoadResult> LoadAsync(string pluginPath, CancellationToken cancel, TimeSpan? timeoutOverride = null)
    {
        var limit = timeoutOverride ?? Timeout;
        var output = Path.Combine(Path.GetTempPath(), "WhatPlugin", Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);

        var workingDir = Directory.Exists(pluginPath) ? pluginPath : Path.GetDirectoryName(pluginPath)!;
        var info = new ProcessStartInfo(ScannerPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 依赖 dll 常放在插件旁边，与 DAW 的加载环境保持接近。
            WorkingDirectory = workingDir,
        };
        info.ArgumentList.Add(pluginPath);
        info.ArgumentList.Add(output);

        try
        {
            using var process = new Process { StartInfo = info };
            try
            {
                process.Start();
            }
            catch (Win32Exception e)
            {
                return new LoadResult(LoadOutcome.Failed, [], "无法启动扫描器: " + e.Message);
            }

            // 必须持续读取输出，否则插件大量打印时管道写满会把进程卡住。
            _ = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            _ = process.StandardError.ReadToEndAsync(CancellationToken.None);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeoutCts.CancelAfter(limit);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                cancel.ThrowIfCancellationRequested();
                return new LoadResult(LoadOutcome.TimedOut, [], $"加载超过 {limit.TotalSeconds:0} 秒");
            }

            return process.ExitCode switch
            {
                0 when File.Exists(output) => ParseOutput(await File.ReadAllTextAsync(output, CancellationToken.None)),
                2 => new LoadResult(LoadOutcome.NoPlugins, [], "文件中没有可识别的插件"),
                5 => new LoadResult(LoadOutcome.Failed, [], "模块无法加载，可能缺少依赖文件"),
                6 => new LoadResult(LoadOutcome.Failed, [], "模块没有提供 VST3 工厂"),
                var code => new LoadResult(LoadOutcome.Failed, [], $"扫描器异常退出，退出码 0x{code:X8}"),
            };
        }
        finally
        {
            TryDelete(output);
        }
    }

    public static LoadResult ParseOutput(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var classes = new List<PluginClassInfo>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var category = item.GetProperty("category").GetString() ?? "";
                classes.Add(new PluginClassInfo(
                    item.GetProperty("name").GetString() ?? "",
                    item.GetProperty("vendor").GetString() ?? "",
                    item.GetProperty("version").GetString() ?? "",
                    category,
                    StaticInspector.IsInstrumentCategory(category),
                    InfoSource.PluginLoad));
            }
            return classes.Count > 0
                ? new LoadResult(LoadOutcome.Success, classes, null)
                : new LoadResult(LoadOutcome.NoPlugins, [], "文件中没有可识别的插件");
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new LoadResult(LoadOutcome.Failed, [], "扫描结果无法解析: " + e.Message);
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // 进程已经退出。
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 系统临时目录里的残留无害。
        }
    }
}
