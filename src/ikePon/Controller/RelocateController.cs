using System.IO;
using ikePon.Model;

namespace ikePon.Controller;

/// <summary>
/// プロジェクト読み込み時に行方不明ファイルを自律的に検索・再リンクする。
/// </summary>
public sealed class RelocateController
{
    /// <summary>進捗・結果メッセージ（UIスレッドから購読すること）</summary>
    public event Action<string>? StatusMessage;

    /// <summary>Phase 2 でユーザーが手動指定したファイルのフォルダ。settings 更新用。</summary>
    public string? ManuallySelectedDirectory { get; private set; }

    /// <summary>行方不明ファイルが1本以上あった場合 true</summary>
    public bool AnyMissingFound { get; private set; }

    /// <summary>
    /// 行方不明ファイルの自動探索・再リンクを実行する。
    /// </summary>
    /// <param name="project">読み込み済みプロジェクト（FilePath がインプレースで更新される）</param>
    /// <param name="projectFilePath">ikp ファイルの絶対パス（フォルダ探索の基点）</param>
    /// <param name="lastResourceDir">最後に成功した素材フォルダ（settings から）</param>
    /// <param name="promptUserForFile">Phase 2 でユーザーにファイルを指定させるコールバック</param>
    /// <returns>最終的に解決できなかったパッドの (bank, pad) リスト</returns>
    public async Task<IReadOnlyList<(int bank, int pad)>> RelocateAsync(
        ProjectData project,
        string? projectFilePath,
        string lastResourceDir,
        Func<string, Task<string?>> promptUserForFile)
    {
        ManuallySelectedDirectory = null;
        var missing = CollectMissingPads(project);
        AnyMissingFound = missing.Count > 0;

        if (missing.Count == 0) return [];

        // ユニークな不明パス数（同一ファイルを複数パッドで使用している場合を考慮）
        int total = missing.Select(x => x.origPath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        StatusMessage?.Invoke(L.F("Str_Ctrl_RelocateStart", total));
        Logger.Log($"[Relocate] Start: {total} unique missing paths");

        // ─── Phase 1: ヒントフォルダを自動探索（バックグラウンドスレッドで進捗をリアルタイム更新）
        var hintDirs = BuildHintDirs(projectFilePath, lastResourceDir);

        await Task.Run(() =>
        {
            foreach (var hintDir in hintDirs)
            {
                if (resolved.Count >= total) break;

                Logger.Log($"[Relocate] Searching: {hintDir}");
                StatusMessage?.Invoke(L.F("Str_Ctrl_RelocateProgressScan", resolved.Count, total));

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var index = BuildFileIndex(hintDir, cts.Token);

                Logger.Log($"[Relocate] FileIndex built: {index.Count} unique names ({hintDir})");

                foreach (var mp in missing.DistinctBy(x => x.origPath, StringComparer.OrdinalIgnoreCase))
                {
                    if (resolved.Contains(mp.origPath)) continue;

                    string fileName  = Path.GetFileName(mp.origPath);
                    string parentDir = Path.GetFileName(Path.GetDirectoryName(mp.origPath) ?? "") ?? "";

                    var (found, isPartial) = FindInIndex(index, fileName, parentDir);
                    if (found != null)
                    {
                        if (isPartial)
                            Logger.Log($"[Relocate] Partial match: {mp.origPath} → {found}");

                        UpdatePaths(project, mp.origPath, found);
                        resolved.Add(mp.origPath);

                        // バックグラウンドスレッドから Dispatcher.Invoke 経由でUI更新
                        StatusMessage?.Invoke(
                            L.F("Str_Ctrl_RelocateProgress", resolved.Count, total));
                    }
                }
            }
        });

        // ─── Phase 2: 手動起点による一括リロケート ───────────────────────
        var unresolved = missing
            .DistinctBy(x => x.origPath, StringComparer.OrdinalIgnoreCase)
            .Where(x => !resolved.Contains(x.origPath))
            .ToList();

        if (unresolved.Count > 0)
        {
            StatusMessage?.Invoke(L.F("Str_Ctrl_RelocateManual", unresolved.Count));

            string? selected = await promptUserForFile(unresolved[0].origPath);

            if (selected != null && File.Exists(selected))
            {
                string newHintDir = Path.GetDirectoryName(selected)!;
                ManuallySelectedDirectory = newHintDir;

                // まず手動指定ファイルを反映
                UpdatePaths(project, unresolved[0].origPath, selected);
                resolved.Add(unresolved[0].origPath);

                // 残りを新ヒントフォルダで一括検索
                if (unresolved.Count > 1)
                {
                    Logger.Log($"[Relocate] Phase2 batch search: {newHintDir}");
                    using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var index = await Task.Run(() => BuildFileIndex(newHintDir, cts2.Token));

                    foreach (var mp in unresolved.Skip(1))
                    {
                        if (resolved.Contains(mp.origPath)) continue;

                        string fileName  = Path.GetFileName(mp.origPath);
                        string parentDir = Path.GetFileName(Path.GetDirectoryName(mp.origPath) ?? "") ?? "";

                        var (found, _) = FindInIndex(index, fileName, parentDir);
                        if (found != null)
                        {
                            UpdatePaths(project, mp.origPath, found);
                            resolved.Add(mp.origPath);
                        }
                    }
                }
            }
        }

        // ─── 最終確認・結果メッセージ ─────────────────────────────────────
        var finalMissing = new List<(int bank, int pad)>();
        for (int b = 0; b < project.Banks.Length; b++)
            for (int p = 0; p < project.Banks[b].Pads.Length; p++)
            {
                string? fp = project.Banks[b].Pads[p].FilePath;
                if (!string.IsNullOrEmpty(fp) && !File.Exists(fp))
                    finalMissing.Add((b, p));
            }

        if (finalMissing.Count == 0)
            StatusMessage?.Invoke(L.F("Str_Ctrl_RelocateDone", resolved.Count));
        else
            StatusMessage?.Invoke(L.F("Str_Ctrl_RelocateMissing", finalMissing.Count));

        Logger.Log($"[Relocate] Done: resolved={resolved.Count}, stillMissing={finalMissing.Count}");
        return finalMissing;
    }

    // ── private helpers ──────────────────────────────────────────────────

    private static List<(int bank, int pad, string origPath)> CollectMissingPads(ProjectData project)
    {
        var result = new List<(int, int, string)>();
        for (int b = 0; b < project.Banks.Length; b++)
            for (int p = 0; p < project.Banks[b].Pads.Length; p++)
            {
                string? fp = project.Banks[b].Pads[p].FilePath;
                if (!string.IsNullOrEmpty(fp) && !File.Exists(fp))
                    result.Add((b, p, fp));
            }
        return result;
    }

    private static List<string> BuildHintDirs(string? projectFilePath, string lastResourceDir)
    {
        var dirs = new List<string>();

        if (!string.IsNullOrEmpty(projectFilePath))
        {
            string? dir = Path.GetDirectoryName(projectFilePath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                dirs.Add(dir);
        }

        if (!string.IsNullOrEmpty(lastResourceDir) && Directory.Exists(lastResourceDir) &&
            !dirs.Contains(lastResourceDir, StringComparer.OrdinalIgnoreCase))
            dirs.Add(lastResourceDir);

        return dirs;
    }

    private static Dictionary<string, List<string>> BuildFileIndex(
        string rootDir, CancellationToken ct = default)
    {
        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        const int maxFiles = 100_000; // 巨大ディレクトリでの暴走防止
        int count = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(rootDir, "*", SearchOption.AllDirectories))
            {
                if (ct.IsCancellationRequested)
                {
                    Logger.Log($"[Relocate] FileIndex timeout after {count} files: {rootDir}");
                    break;
                }
                if (++count > maxFiles)
                {
                    Logger.Log($"[Relocate] FileIndex maxFiles limit ({maxFiles}) reached: {rootDir}");
                    break;
                }
                string name = Path.GetFileName(file);
                if (!index.TryGetValue(name, out var list))
                    index[name] = list = [];
                list.Add(file);
            }
        }
        catch (OperationCanceledException)
        {
            Logger.Log($"[Relocate] FileIndex cancelled after {count} files: {rootDir}");
        }
        catch (Exception ex)
        {
            Logger.Log($"[Relocate] FileIndex error ({rootDir}): {ex.Message}");
        }
        return index;
    }

    /// <summary>ファイル名・親フォルダ名でマッチングを行う（完全一致優先）</summary>
    private static (string? path, bool isPartial) FindInIndex(
        Dictionary<string, List<string>> index, string fileName, string parentDir)
    {
        if (!index.TryGetValue(fileName, out var candidates)) return (null, false);

        // 完全マッチ: ファイル名 + 親フォルダ名が一致
        if (!string.IsNullOrEmpty(parentDir))
        {
            foreach (var c in candidates)
            {
                string cp = Path.GetFileName(Path.GetDirectoryName(c) ?? "") ?? "";
                if (string.Equals(cp, parentDir, StringComparison.OrdinalIgnoreCase))
                    return (c, false);
            }
        }

        // 前方一致（最初にヒットしたもの）
        return candidates.Count > 0 ? (candidates[0], true) : (null, false);
    }

    private static void UpdatePaths(ProjectData project, string oldPath, string newPath)
    {
        for (int b = 0; b < project.Banks.Length; b++)
            for (int p = 0; p < project.Banks[b].Pads.Length; p++)
                if (string.Equals(project.Banks[b].Pads[p].FilePath, oldPath, StringComparison.OrdinalIgnoreCase))
                    project.Banks[b].Pads[p].FilePath = newPath;
    }
}
