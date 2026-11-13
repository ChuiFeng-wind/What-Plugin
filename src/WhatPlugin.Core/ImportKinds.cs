namespace WhatPlugin.Core;

/// <summary>“导入备份”能读的几种文件。Share 是评价和混音笔记放在一起的分享文件。</summary>
public enum ImportKind { Backup, Reviews, Notes, Share }

public static class ImportKinds
{
    /// <summary>按扩展名分辨。认不出的当完整备份读，格式不对时由备份的读取报错。</summary>
    public static ImportKind Of(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(ReviewShare.Extension, StringComparison.OrdinalIgnoreCase))
            return ImportKind.Reviews;
        if (extension.Equals(NoteShare.Extension, StringComparison.OrdinalIgnoreCase))
            return ImportKind.Notes;
        if (extension.Equals(ShareBundles.Extension, StringComparison.OrdinalIgnoreCase))
            return ImportKind.Share;
        return ImportKind.Backup;
    }
}
