using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace WhatPlugin.Core;

/// <summary>
/// 把插件文件移到系统的回收站（Windows）或废纸篓（Mac），以及放回原处。
/// 每个路径都先过 <see cref="RemovalGuard"/>，不在插件文件夹里的一律拒绝。
/// </summary>
public interface IPluginTrash
{
    /// <summary>“回收站”或“废纸篓”。</summary>
    string Name { get; }

    Task<IReadOnlyList<FileOpResult>> MoveAsync(IReadOnlyList<string> paths, IReadOnlyList<string> extraRoots);

    Task<FileOpResult> RestoreAsync(RemovedFile file, IReadOnlyList<string> extraRoots);
}

public static class PluginTrash
{
    /// <summary>这台电脑上系统回收处的叫法。</summary>
    public static string Name => AppPlatform.IsMac ? "废纸篓" : "回收站";

    /// <summary>回收处已经清空（或文件被拿走）时的错误文字。界面据此删掉再也用不上的恢复记录。</summary>
    public static string Gone => $"{Name}里已经没有这个文件";

    public static IPluginTrash ForThisComputer()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsPluginTrash();
        if (OperatingSystem.IsMacOS())
            return new MacPluginTrash();
        return new UnsupportedPluginTrash();
    }
}

/// <summary>Windows：送进回收站，Program Files 等位置按需弹一次 UAC（见 <see cref="FileOpRunner"/>）。</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPluginTrash : IPluginTrash
{
    public string Name => "回收站";

    public Task<IReadOnlyList<FileOpResult>> MoveAsync(IReadOnlyList<string> paths, IReadOnlyList<string> extraRoots) =>
        FileOpRunner.RunAsync(new FileOpRequest(FileOpKind.Recycle, paths, extraRoots));

    public async Task<FileOpResult> RestoreAsync(RemovedFile file, IReadOnlyList<string> extraRoots) =>
        (await FileOpRunner.RunAsync(new FileOpRequest(FileOpKind.Restore, [file.Path], extraRoots)))[0];
}

/// <summary>Linux 等还没有实现的系统：什么都不动，直接报告做不到。</summary>
public sealed class UnsupportedPluginTrash : IPluginTrash
{
    private const string Error = "这个系统上还不能移到回收站";

    public string Name => "回收站";

    public Task<IReadOnlyList<FileOpResult>> MoveAsync(IReadOnlyList<string> paths, IReadOnlyList<string> extraRoots) =>
        Task.FromResult<IReadOnlyList<FileOpResult>>(paths.Select(p => new FileOpResult(p, false, Error)).ToList());

    public Task<FileOpResult> RestoreAsync(RemovedFile file, IReadOnlyList<string> extraRoots) =>
        Task.FromResult(new FileOpResult(file.Path, false, Error));
}

/// <summary>
/// Mac：用系统接口（NSFileManager）移到废纸篓，和在访达里“移到废纸篓”一样，可以在废纸篓里“放回原处”。
/// /Library/Audio/Plug-Ins 下的文件属于系统管理员，本软件没有权限，就请访达来做：
/// 访达会自己弹出系统的管理员授权框，本软件看不到也不保存密码。
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacPluginTrash : IPluginTrash
{
    private const int CancelledByUser = -128;

    public string Name => "废纸篓";

    public Task<IReadOnlyList<FileOpResult>> MoveAsync(IReadOnlyList<string> paths, IReadOnlyList<string> extraRoots) =>
        Task.Run(() => Move(paths, extraRoots));

    public Task<FileOpResult> RestoreAsync(RemovedFile file, IReadOnlyList<string> extraRoots) =>
        Task.Run(() => Restore(file, extraRoots));

    private static IReadOnlyList<FileOpResult> Move(IReadOnlyList<string> paths, IReadOnlyList<string> extraRoots)
    {
        var results = new Dictionary<string, FileOpResult>(StringComparer.Ordinal);
        var needFinder = new List<string>();
        foreach (var path in paths)
        {
            var rejected = RemovalGuard.Reject(path, extraRoots);
            if (rejected != null)
            {
                results[path] = new FileOpResult(path, false, rejected);
                continue;
            }
            var native = MacFileManager.Trash(path);
            if (native.Denied)
                needFinder.Add(path);
            else
                results[path] = Checked(path, native.Ok, native.TrashPath, native.Error);
        }

        if (needFinder.Count > 0)
        {
            var (lines, failure) = Osascript(FinderDeleteScript, needFinder);
            var parsed = failure == null ? FinderOutput.Parse(lines, needFinder.Count) : [];
            for (var i = 0; i < needFinder.Count; i++)
            {
                var path = needFinder[i];
                if (failure != null || i >= parsed.Count)
                {
                    results[path] = new FileOpResult(path, false, failure ?? "访达没有返回结果");
                    continue;
                }
                var line = parsed[i];
                results[path] = line.Ok
                    ? Checked(path, true, line.Text, null)
                    : new FileOpResult(path, false, line.Code == CancelledByUser ? "已取消" : "访达没能移动：" + line.Text);
            }
        }
        return paths.Select(p => results[p]).ToList();
    }

    /// <summary>以磁盘为准：原位置真的空了才算成功；废纸篓里找不到时标为未确认。</summary>
    private static FileOpResult Checked(string path, bool ok, string? trashPath, string? error)
    {
        if (!ok)
            return new FileOpResult(path, false, error ?? "没能移到废纸篓");
        if (RemovalPlanner.PathExists(path))
            return new FileOpResult(path, false, "文件仍在原位，可能正在被占用");
        var found = !string.IsNullOrEmpty(trashPath) && RemovalPlanner.PathExists(trashPath);
        return new FileOpResult(path, true, Unconfirmed: !found, TrashPath: found ? trashPath : null);
    }

    private static FileOpResult Restore(RemovedFile file, IReadOnlyList<string> extraRoots)
    {
        var path = file.Path;
        var rejected = RemovalGuard.Reject(path, extraRoots, mustExist: false);
        if (rejected != null)
            return new FileOpResult(path, false, rejected);
        var source = file.TrashPath;
        if (!TrashLocation.IsInTrash(source, path) || !RemovalPlanner.PathExists(source))
            return new FileOpResult(path, false, PluginTrash.Gone);
        if (RemovalPlanner.PathExists(path))
            return new FileOpResult(path, false, "原位置已经有同名文件");

        var folder = Path.GetDirectoryName(path)!;
        try
        {
            Directory.CreateDirectory(folder);
            if (Directory.Exists(source))
                Directory.Move(source, path);
            else
                File.Move(source, path);
            return new FileOpResult(path, true);
        }
        catch (UnauthorizedAccessException)
        {
            // 系统插件文件夹没有写权限，请访达放回去，由它弹出管理员授权。
        }
        catch (IOException error)
        {
            return new FileOpResult(path, false, error.Message);
        }

        if (!Directory.Exists(folder))
            return new FileOpResult(path, false, "原来的文件夹已经不在了，没有权限重新建立");
        var (lines, failure) = Osascript(FinderRestoreScript, [source, folder, Path.GetFileName(path)]);
        if (failure != null)
            return new FileOpResult(path, false, failure);
        var line = FinderOutput.Parse(lines, 1).FirstOrDefault();
        if (line is { Ok: false })
            return new FileOpResult(path, false, line.Code == CancelledByUser ? "已取消" : "访达没能放回：" + line.Text);
        return RemovalPlanner.PathExists(path)
            ? new FileOpResult(path, true)
            : new FileOpResult(path, false, "访达没有把文件放回原位");
    }

    // 路径作为参数传给脚本，不拼进脚本文字，路径里有引号也不会变成命令。
    private static readonly string[] FinderDeleteScript =
    [
        "on run argv",
        "  set output to \"\"",
        "  repeat with itemPath in argv",
        "    try",
        "      set target to (POSIX file (itemPath as text)) as alias",
        "      tell application \"Finder\" to set trashed to delete target",
        "      set output to output & \"ok\" & tab & \"0\" & tab & (POSIX path of (trashed as alias)) & linefeed",
        "    on error message number code",
        "      set output to output & \"error\" & tab & code & tab & message & linefeed",
        "    end try",
        "  end repeat",
        "  return output",
        "end run",
    ];

    private static readonly string[] FinderRestoreScript =
    [
        "on run argv",
        "  try",
        "    set source to (POSIX file (item 1 of argv)) as alias",
        "    set destination to (POSIX file (item 2 of argv)) as alias",
        "    tell application \"Finder\"",
        "      set moved to move source to destination",
        "      if name of moved is not (item 3 of argv) then set name of moved to (item 3 of argv)",
        "    end tell",
        "    return \"ok\" & tab & \"0\" & tab & \"\"",
        "  on error message number code",
        "    return \"error\" & tab & code & tab & message",
        "  end try",
        "end run",
    ];

    /// <returns>输出的各行；启动失败时 Failure 是原因。</returns>
    private static (IReadOnlyList<string> Lines, string? Failure) Osascript(IReadOnlyList<string> script, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo("/usr/bin/osascript")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var line in script)
        {
            start.ArgumentList.Add("-e");
            start.ArgumentList.Add(line);
        }
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start);
            if (process == null)
                return ([], "无法请访达处理");
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            // 用户可能要花时间在授权框里输密码。
            if (!process.WaitForExit(TimeSpan.FromMinutes(10)))
            {
                process.Kill();
                return ([], "等待访达超时");
            }
            if (process.ExitCode != 0)
                return ([], "访达没有完成：" + error.Result.Trim());
            return (output.Split('\n', StringSplitOptions.RemoveEmptyEntries), null);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return ([], "无法请访达处理：" + e.Message);
        }
    }
}

/// <summary>访达脚本每行一条结果：“ok/error、错误号、废纸篓里的路径或错误文字”，用制表符隔开。</summary>
public static class FinderOutput
{
    public sealed record Line(bool Ok, int Code, string Text);

    public static IReadOnlyList<Line> Parse(IEnumerable<string> lines, int expected) =>
        lines.Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .Take(expected)
            .Select(l =>
            {
                var parts = l.Split('\t', 3);
                var ok = parts[0] == "ok";
                var code = parts.Length > 1 && int.TryParse(parts[1], out var number) ? number : 0;
                var text = parts.Length > 2 ? parts[2] : "";
                // 插件包是文件夹，访达给的路径末尾带斜杠。
                if (ok && text.Length > 1)
                    text = text.TrimEnd('/');
                return new Line(ok, code, text);
            })
            .ToList();
}

/// <summary>恢复前检查记下的废纸篓位置：只认废纸篓里的同类插件，数据库被改过也不会搬动别的东西。</summary>
public static class TrashLocation
{
    public static bool IsInTrash(string trashPath, string originalPath, string? home = null)
    {
        if (string.IsNullOrEmpty(trashPath) || !Path.IsPathFullyQualified(trashPath))
            return false;
        var full = Path.GetFullPath(trashPath);
        if (!string.Equals(full, Path.TrimEndingDirectorySeparator(trashPath), StringComparison.Ordinal))
            return false;
        if (!string.Equals(Path.GetExtension(full), Path.GetExtension(originalPath), StringComparison.OrdinalIgnoreCase))
            return false;
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var userTrash = Path.Combine(home, ".Trash") + "/";
        // 其他磁盘上的废纸篓在 /Volumes/<盘>/.Trashes/<用户编号>/ 里。
        return full.StartsWith(userTrash, StringComparison.Ordinal) || full.Contains("/.Trashes/", StringComparison.Ordinal);
    }
}

/// <summary>通过 Objective-C 运行时调用 NSFileManager 的 trashItemAtURL。</summary>
[SupportedOSPlatform("macos")]
internal static class MacFileManager
{
    // NSCocoaErrorDomain 的“没有读/写权限”，以及 POSIX 的 EPERM、EACCES。
    private const long NoWritePermission = 513, NoReadPermission = 257, PosixPermission = 1, PosixAccess = 13;

    private static readonly Lazy<bool> FoundationLoaded = new(() =>
    {
        // 测试进程里没有加载 Foundation，objc_getClass 会找不到 NSFileManager。
        NativeLibrary.Load("/System/Library/Frameworks/Foundation.framework/Foundation");
        return true;
    });

    public readonly record struct Outcome(bool Ok, bool Denied, string? TrashPath, string? Error);

    public static Outcome Trash(string path)
    {
        _ = FoundationLoaded.Value;
        var pool = Objc.objc_autoreleasePoolPush();
        try
        {
            var text = Objc.Send(Objc.objc_getClass("NSString"), Objc.sel_registerName("stringWithUTF8String:"), Encoding.UTF8.GetBytes(path + "\0"));
            var url = Objc.Send(Objc.objc_getClass("NSURL"), Objc.sel_registerName("fileURLWithPath:"), text);
            var manager = Objc.Send(Objc.objc_getClass("NSFileManager"), Objc.sel_registerName("defaultManager"));
            if (url == IntPtr.Zero || manager == IntPtr.Zero)
                return new Outcome(false, false, null, "系统接口不可用");
            var ok = Objc.SendTrash(manager, Objc.sel_registerName("trashItemAtURL:resultingItemURL:error:"), url, out var resulting, out var error);
            if (ok)
            {
                var trashPath = resulting == IntPtr.Zero ? null : String(Objc.Send(resulting, Objc.sel_registerName("path")));
                return new Outcome(true, false, trashPath, null);
            }
            if (error == IntPtr.Zero)
                return new Outcome(false, false, null, "没能移到废纸篓");
            var code = (long)Objc.Send(error, Objc.sel_registerName("code"));
            var domain = String(Objc.Send(error, Objc.sel_registerName("domain")));
            var message = String(Objc.Send(error, Objc.sel_registerName("localizedDescription")));
            var denied = domain == "NSPOSIXErrorDomain"
                ? code is PosixPermission or PosixAccess
                : code is NoWritePermission or NoReadPermission;
            return new Outcome(false, denied, null, message ?? $"没能移到废纸篓（{domain} {code}）");
        }
        finally
        {
            Objc.objc_autoreleasePoolPop(pool);
        }
    }

    private static string? String(IntPtr nsString)
    {
        if (nsString == IntPtr.Zero)
            return null;
        var utf8 = Objc.Send(nsString, Objc.sel_registerName("UTF8String"));
        return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
    }

    private static class Objc
    {
        private const string Lib = "/usr/lib/libobjc.A.dylib";

        [DllImport(Lib)]
        public static extern IntPtr objc_getClass(string name);

        [DllImport(Lib)]
        public static extern IntPtr sel_registerName(string name);

        [DllImport(Lib)]
        public static extern IntPtr objc_autoreleasePoolPush();

        [DllImport(Lib)]
        public static extern void objc_autoreleasePoolPop(IntPtr pool);

        [DllImport(Lib, EntryPoint = "objc_msgSend")]
        public static extern IntPtr Send(IntPtr receiver, IntPtr selector);

        [DllImport(Lib, EntryPoint = "objc_msgSend")]
        public static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr argument);

        [DllImport(Lib, EntryPoint = "objc_msgSend")]
        public static extern IntPtr Send(IntPtr receiver, IntPtr selector, byte[] utf8);

        [DllImport(Lib, EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool SendTrash(IntPtr receiver, IntPtr selector, IntPtr url, out IntPtr resultingUrl, out IntPtr error);
    }
}
