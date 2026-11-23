using System.IO;

namespace WhatPlugin.Core;

/// <summary>工程页左侧的一个文件夹。数量包含所有下级。</summary>
public sealed record ProjectFolderNode(
    string Path,
    string Name,
    int ProjectCount,
    int MissingProjects,
    IReadOnlyList<ProjectFolderNode> Children);

/// <summary>
/// 把工程按文件夹分层。只放一个工程的末级文件夹不单独成层，算进上一级，
/// 否则“一首歌一个文件夹”的习惯会让树和工程列表一样长。
/// 中间只起过渡作用的文件夹合并显示，比如“客户A / 2026”。
/// </summary>
public static class ProjectFolderTree
{
    private const string NameSeparator = " / ";

    /// <summary>折叠的文件夹和树是否显示，存在设置里（两个界面共用）。</summary>
    public const string CollapsedSetting = "projects.collapsedFolders";

    public const string VisibleSetting = "projects.treeVisible";

    public static IReadOnlyList<ProjectFolderNode> Build(IEnumerable<string> roots, IEnumerable<ProjectEntry> projects)
    {
        var tops = OutermostRoots(roots).ToDictionary(r => r, r => new Draft(r, RootName(r)), StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
        {
            var folder = Clean(project.Folder);
            var root = tops.Keys.FirstOrDefault(r => Contains(r, folder));
            if (root == null)
            {
                // 工程不在任何已添加的文件夹下时，它所在的文件夹自成一组。
                root = folder;
                tops[root] = new Draft(root, RootName(root));
            }
            tops[root].Add(folder, project.MissingCount > 0);
        }
        return tops.Values
            .Where(t => t.Count > 0)
            .OrderBy(t => t.Name, StringComparer.CurrentCulture)
            .Select(t => new ProjectFolderNode(t.Path, t.Name, t.Count, t.Missing, Children(t)))
            .ToList();
    }

    /// <summary>folder 是 parent 本身或它的下级。按路径分段比较，“混音2”不算“混音”的下级。</summary>
    public static bool Contains(string parent, string folder)
    {
        var p = Clean(parent);
        var f = Clean(folder);
        return f.Equals(p, StringComparison.OrdinalIgnoreCase)
            || f.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<ProjectFolderNode> Children(Draft parent) =>
        parent.Children.Values
            .Where(c => c.Count > 1)
            .OrderBy(c => c.Name, StringComparer.CurrentCulture)
            .Select(Convert)
            .ToList();

    private static ProjectFolderNode Convert(Draft draft)
    {
        var name = draft.Name;
        // 所有工程都在唯一的下级里时，这一层没有分类意义。
        while (draft.Children.Values.FirstOrDefault(c => c.Count == draft.Count) is { } only)
        {
            name += NameSeparator + only.Name;
            draft = only;
        }
        return new ProjectFolderNode(draft.Path, name, draft.Count, draft.Missing, Children(draft));
    }

    private static IEnumerable<string> OutermostRoots(IEnumerable<string> roots)
    {
        var cleaned = roots.Select(Clean).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(r => r.Length).ToList();
        var kept = new List<string>();
        foreach (var root in cleaned)
        {
            if (!kept.Any(k => Contains(k, root)))
                kept.Add(root);
        }
        return kept;
    }

    private static string Clean(string path)
    {
        var normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return normalized.TrimEnd(Path.DirectorySeparatorChar);
    }

    private static string RootName(string root)
    {
        var name = Path.GetFileName(root);
        return name.Length > 0 ? name : root;
    }

    private sealed class Draft(string path, string name)
    {
        public string Path { get; } = path;
        public string Name { get; } = name;
        public int Count { get; private set; }
        public int Missing { get; private set; }
        public Dictionary<string, Draft> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string folder, bool missing)
        {
            Count++;
            if (missing)
                Missing++;
            var rest = folder.Length > Path.Length ? folder[(Path.Length + 1)..] : "";
            if (rest.Length == 0)
                return;
            var segment = rest.Split(System.IO.Path.DirectorySeparatorChar)[0];
            if (!Children.TryGetValue(segment, out var child))
                Children[segment] = child = new Draft(Path + System.IO.Path.DirectorySeparatorChar + segment, segment);
            child.Add(folder, missing);
        }
    }
}
