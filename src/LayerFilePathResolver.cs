using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using PaintDotNet;

namespace LayerChannelLive;

/// <summary>
/// 图层源文件解析器。
/// 
/// 负责为 Paint.NET 文档中的图层（尤其是从文件导入的 DDS 贴图图层）
/// 智能解析出其在本地磁盘上对应的原始图片文件路径。
/// </summary>
internal static class LayerFilePathResolver
{
    // 弱引用缓存：图层对象 -> 已解析的完整文件路径（自动 GC 回收，无泄漏风险）
    private static readonly ConditionalWeakTable<Layer, string> LayerPathCache = new();

    // 运行时记录的最近导入文件列表（保序，最新在前）
    private static readonly List<string> RecentImportedFiles = new();

    // 运行时记录的候选目录集合
    private static readonly HashSet<string> KnownDirectories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 显式为图层登记或更新对应的源文件路径。
    /// </summary>
    public static void RegisterLayerPath(Layer layer, string filePath)
    {
        if (layer == null || string.IsNullOrWhiteSpace(filePath)) return;
        try
        {
            if (File.Exists(filePath))
            {
                string fullPath = Path.GetFullPath(filePath);
                LayerPathCache.AddOrUpdate(layer, fullPath);
                RecordImportedFile(fullPath);
            }
        }
        catch { }
    }

    /// <summary>
    /// 记录最近导入或拖拽的文件路径。
    /// </summary>
    public static void RecordImportedFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        try
        {
            if (File.Exists(filePath))
            {
                string full = Path.GetFullPath(filePath);
                lock (RecentImportedFiles)
                {
                    RecentImportedFiles.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
                    RecentImportedFiles.Insert(0, full);
                    if (RecentImportedFiles.Count > 40)
                    {
                        RecentImportedFiles.RemoveAt(RecentImportedFiles.Count - 1);
                    }
                }

                string? dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    lock (KnownDirectories)
                    {
                        KnownDirectories.Add(dir);
                    }
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// 解析图层对应的源文件路径。
    /// </summary>
    public static string? Resolve(Layer? layer, object? documentWorkspace, Document? document)
    {
        if (layer == null)
        {
            return TryGetWorkspaceFilePath(documentWorkspace);
        }

        // 1. 检查弱引用缓存
        if (LayerPathCache.TryGetValue(layer, out string? cached) && !string.IsNullOrWhiteSpace(cached))
        {
            if (File.Exists(cached)) return cached;
            LayerPathCache.Remove(layer);
        }

        string? docPath = TryGetWorkspaceFilePath(documentWorkspace);
        string rawName = layer.Name ?? "";

        // 2. 如果图层名本身就是一个绝对路径且文件存在
        if (!string.IsNullOrWhiteSpace(rawName))
        {
            try
            {
                if (File.Exists(rawName))
                {
                    string full = Path.GetFullPath(rawName);
                    LayerPathCache.AddOrUpdate(layer, full);
                    RecordImportedFile(full);
                    return full;
                }
            }
            catch { }
        }

        // 判断是否是背景图层
        bool isBackgroundLayer = false;
        if (document != null && document.Layers.Count > 0)
        {
            if (ReferenceEquals(document.Layers[0], layer) || layer.IsBackground)
            {
                isBackgroundLayer = true;
            }
        }

        // 3. 提取图层名称前缀
        // Paint.NET 导入图层时命名规则为 "{文件名去掉扩展名}: {原图层名}"（英文冒号）
        // 或 "{文件名去掉扩展名}：{原图层名}"（中文系统全角冒号，如 "6f299713-41334-0-NormalMap：背景"）
        string cleaned = rawName.Trim();
        int colonIdx = cleaned.IndexOfAny(new[] { ':', '：' });
        bool hasColon = colonIdx > 0;
        string prefix = hasColon ? cleaned.Substring(0, colonIdx).Trim() : cleaned;

        // 如果纯粹是背景图层且名字就是默认的“背景”/“Background”
        if (isBackgroundLayer && !hasColon && (prefix == "背景" || prefix.Equals("Background", StringComparison.OrdinalIgnoreCase)))
        {
            if (!string.IsNullOrWhiteSpace(docPath) && File.Exists(docPath))
            {
                return docPath;
            }
        }

        // 4. 生成候选文件名列表
        var candidates = new List<string>();
        void AddCandidate(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            if (!candidates.Contains(name, StringComparer.OrdinalIgnoreCase))
                candidates.Add(name);
        }

        if (prefix.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
        {
            AddCandidate(prefix);
            string noExt = Path.GetFileNameWithoutExtension(prefix);
            AddCandidate(noExt + ".dds");
            AddCandidate(noExt + ".DDS");
        }
        else
        {
            AddCandidate(prefix + ".dds");
            AddCandidate(prefix + ".DDS");
            AddCandidate(prefix);
            if (prefix.Contains('.'))
            {
                AddCandidate(Path.ChangeExtension(prefix, ".dds"));
            }
        }

        if (hasColon)
        {
            string suffix = cleaned.Substring(colonIdx + 1).Trim();
            if (!string.IsNullOrWhiteSpace(suffix) &&
                !suffix.Equals("背景", StringComparison.OrdinalIgnoreCase) &&
                !suffix.Equals("Background", StringComparison.OrdinalIgnoreCase))
            {
                AddCandidate(suffix);
                AddCandidate(suffix + ".dds");
                AddCandidate(suffix + ".DDS");
            }
        }

        // 5. 在运行时记录的最近导入文件中精确匹配
        lock (RecentImportedFiles)
        {
            foreach (var file in RecentImportedFiles)
            {
                if (File.Exists(file))
                {
                    string fn = Path.GetFileName(file);
                    string fnNoExt = Path.GetFileNameWithoutExtension(file);
                    if (string.Equals(fnNoExt, prefix, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(fn, prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        LayerPathCache.AddOrUpdate(layer, file);
                        return file;
                    }
                    foreach (var c in candidates)
                    {
                        if (string.Equals(fn, c, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(fnNoExt, c, StringComparison.OrdinalIgnoreCase))
                        {
                            LayerPathCache.AddOrUpdate(layer, file);
                            return file;
                        }
                    }
                }
            }
        }

        // 6. 从 Windows 最近文档（Recent）快捷方式中匹配
        string? fromLnk = TryMatchFromWindowsRecent(candidates);
        if (!string.IsNullOrWhiteSpace(fromLnk) && File.Exists(fromLnk))
        {
            LayerPathCache.AddOrUpdate(layer, fromLnk);
            RecordImportedFile(fromLnk);
            return fromLnk;
        }

        // 7. 准备搜索目录集合
        var searchDirs = new List<string>();
        void AddDir(string? d)
        {
            if (string.IsNullOrWhiteSpace(d)) return;
            try
            {
                if (Directory.Exists(d))
                {
                    string full = Path.GetFullPath(d);
                    if (!searchDirs.Contains(full, StringComparer.OrdinalIgnoreCase))
                        searchDirs.Add(full);
                }
            }
            catch { }
        }

        if (!string.IsNullOrWhiteSpace(docPath))
        {
            string? docDir = Path.GetDirectoryName(docPath);
            AddDir(docDir);
            if (!string.IsNullOrWhiteSpace(docDir))
            {
                AddDir(Path.Combine(docDir, "Textures"));
                AddDir(Path.Combine(docDir, "textures"));
                AddDir(Path.Combine(docDir, "Texture"));
                AddDir(Path.Combine(docDir, "Mods"));
                AddDir(Path.Combine(docDir, "mods"));
                string? parentDir = Path.GetDirectoryName(docDir);
                if (!string.IsNullOrWhiteSpace(parentDir))
                {
                    AddDir(parentDir);
                    AddDir(Path.Combine(parentDir, "Textures"));
                    AddDir(Path.Combine(parentDir, "textures"));
                }
            }
        }

        lock (KnownDirectories)
        {
            foreach (var d in KnownDirectories) AddDir(d);
        }

        TryCollectPaintDotNetRecentDirs(searchDirs);

        // 8. 在搜索目录中寻找匹配文件
        foreach (var dir in searchDirs)
        {
            foreach (var c in candidates)
            {
                try
                {
                    string target = Path.Combine(dir, c);
                    if (File.Exists(target))
                    {
                        LayerPathCache.AddOrUpdate(layer, target);
                        RecordImportedFile(target);
                        return target;
                    }
                }
                catch { }
            }

            // 在该目录下枚举 *.dds 做不区分大小写匹配
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.dds"))
                {
                    string fn = Path.GetFileName(file);
                    string fnNoExt = Path.GetFileNameWithoutExtension(file);
                    if (string.Equals(fn, prefix, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(fnNoExt, prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        LayerPathCache.AddOrUpdate(layer, file);
                        RecordImportedFile(file);
                        return file;
                    }
                    foreach (var c in candidates)
                    {
                        if (string.Equals(fn, c, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(fnNoExt, c, StringComparison.OrdinalIgnoreCase))
                        {
                            LayerPathCache.AddOrUpdate(layer, file);
                            RecordImportedFile(file);
                            return file;
                        }
                    }
                }
            }
            catch { }
        }

        // 9. 降级回退：如果是背景图层，使用当前文档的文件路径；如果是导入的非背景图层且未找到，绝不能错误回退到主文档路径！
        if (isBackgroundLayer && !string.IsNullOrWhiteSpace(docPath) && File.Exists(docPath))
        {
            return docPath;
        }

        // 如果图层名和主文档的文件名一致（例如背景图层被复制或更名）
        if (!string.IsNullOrWhiteSpace(docPath) && File.Exists(docPath))
        {
            string docNameNoExt = Path.GetFileNameWithoutExtension(docPath);
            if (string.Equals(prefix, docNameNoExt, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rawName, docNameNoExt, StringComparison.OrdinalIgnoreCase))
            {
                return docPath;
            }
        }

        return null;
    }

    private static string? TryGetWorkspaceFilePath(object? documentWorkspace)
    {
        if (documentWorkspace == null) return null;
        for (Type? t = documentWorkspace.GetType(); t != null; t = t.BaseType)
        {
            var prop = t.GetProperty("FilePath", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (prop?.GetValue(documentWorkspace) is string path && !string.IsNullOrWhiteSpace(path))
            {
                return path;
            }
        }
        return null;
    }

    private static string? TryMatchFromWindowsRecent(List<string> candidates)
    {
        try
        {
            string recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            if (!Directory.Exists(recent)) return null;

            foreach (var c in candidates)
            {
                string lnk = Path.Combine(recent, c + ".lnk");
                if (File.Exists(lnk))
                {
                    string target = ResolveLnk(lnk);
                    if (!string.IsNullOrWhiteSpace(target) && File.Exists(target))
                        return target;
                }
            }

            var recentFiles = new DirectoryInfo(recent).GetFiles("*.lnk")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(25);

            foreach (var fi in recentFiles)
            {
                string lnkName = fi.Name;
                if (!lnkName.EndsWith(".dds.lnk", StringComparison.OrdinalIgnoreCase)) continue;
                string baseName = lnkName.Substring(0, lnkName.Length - 4); // 去掉 .lnk
                foreach (var c in candidates)
                {
                    if (string.Equals(baseName, c, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(Path.GetFileNameWithoutExtension(baseName), c, StringComparison.OrdinalIgnoreCase))
                    {
                        string target = ResolveLnk(fi.FullName);
                        if (!string.IsNullOrWhiteSpace(target) && File.Exists(target))
                            return target;
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private static string ResolveLnk(string path)
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType != null)
            {
                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic shortcut = shell.CreateShortcut(path);
                string target = shortcut.TargetPath;
                if (!string.IsNullOrWhiteSpace(target) && File.Exists(target))
                {
                    return target;
                }
            }
        }
        catch { }

        // 二进制解析 LocalBasePath（无 COM 依赖兜底）
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length > 0x4C)
            {
                uint flags = BitConverter.ToUInt32(bytes, 0x14);
                if ((flags & 2) != 0) // HasLinkInfo
                {
                    int linkInfoOffset = BitConverter.ToInt32(bytes, 0x4C);
                    if (linkInfoOffset > 0 && linkInfoOffset + 0x14 < bytes.Length)
                    {
                        int localBasePathOffset = BitConverter.ToInt32(bytes, linkInfoOffset + 0x10);
                        int pathOffset = linkInfoOffset + localBasePathOffset;
                        if (pathOffset > 0 && pathOffset < bytes.Length)
                        {
                            int end = pathOffset;
                            while (end < bytes.Length && bytes[end] != 0) end++;
                            string res = System.Text.Encoding.Default.GetString(bytes, pathOffset, end - pathOffset);
                            if (File.Exists(res)) return res;
                        }
                    }
                }
            }
        }
        catch { }

        return "";
    }

    private static void TryCollectPaintDotNetRecentDirs(List<string> searchDirs)
    {
        try
        {
            var appSettingsType = Type.GetType("PaintDotNet.Settings.App.AppSettings, paintdotnet");
            if (appSettingsType == null)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    appSettingsType = asm.GetType("PaintDotNet.Settings.App.AppSettings");
                    if (appSettingsType != null) break;
                }
            }
            if (appSettingsType != null)
            {
                var instProp = appSettingsType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                object? inst = instProp?.GetValue(null);
                if (inst != null)
                {
                    var fileField = appSettingsType.GetField("File", BindingFlags.Public | BindingFlags.Instance);
                    object? fileSec = fileField?.GetValue(inst);
                    if (fileSec != null)
                    {
                        var mrField = fileSec.GetType().GetField("MostRecent", BindingFlags.Public | BindingFlags.Instance);
                        object? mrSec = mrField?.GetValue(fileSec);
                        if (mrSec != null)
                        {
                            var pathsField = mrSec.GetType().GetField("Paths", BindingFlags.Public | BindingFlags.Instance);
                            var paths = pathsField?.GetValue(mrSec) as System.Collections.IEnumerable;
                            if (paths != null)
                            {
                                foreach (object item in paths)
                                {
                                    var valProp = item.GetType().GetProperty("Value");
                                    string? p = valProp?.GetValue(item) as string;
                                    if (!string.IsNullOrWhiteSpace(p))
                                    {
                                        string? d = Path.GetDirectoryName(p);
                                        if (!string.IsNullOrWhiteSpace(d) && Directory.Exists(d))
                                        {
                                            if (!searchDirs.Contains(d, StringComparer.OrdinalIgnoreCase))
                                                searchDirs.Add(d);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        catch { }
    }
}
