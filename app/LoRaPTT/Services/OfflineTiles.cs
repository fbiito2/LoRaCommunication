using Microsoft.Maui.Storage;

namespace LoRaPTT.Services;

/// <summary>
/// 離線圖磚檔案庫管理（F-037 階段二）。圖磚存於 AppData/offlinetiles/{layer}/{z}/{x}/{y}.jpg。
/// 下載與線上瀏覽都寫入同一庫，覆蓋自然累積；供地圖離線顯示與設定頁容量管理共用。
/// </summary>
public static class OfflineTiles
{
    /// <summary>離線圖磚根目錄</summary>
    public static string Root => Path.Combine(FileSystem.AppDataDirectory, "offlinetiles");

    /// <summary>
    /// 取得某圖磚的本機檔案路徑。
    /// </summary>
    /// <param name="layer">圖層（如 "sat" 衛星、"emap" 街道）</param>
    /// <param name="z">縮放層級</param>
    /// <param name="x">圖磚 X</param>
    /// <param name="y">圖磚 Y</param>
    /// <returns>檔案完整路徑</returns>
    public static string TilePath(string layer, int z, int x, int y)
        => Path.Combine(Root, layer, z.ToString(), x.ToString(), y + ".jpg");

    /// <summary>
    /// 計算離線圖磚總大小與張數。
    /// </summary>
    /// <returns>(總位元組, 張數)</returns>
    public static (long bytes, int count) GetUsage()
    {
        if (!Directory.Exists(Root)) return (0, 0);
        long bytes = 0;
        int count = 0;
        foreach (var f in Directory.EnumerateFiles(Root, "*.jpg", SearchOption.AllDirectories))
        {
            try { bytes += new FileInfo(f).Length; count++; }
            catch { /* 個別檔讀取失敗略過 */ }
        }
        return (bytes, count);
    }

    /// <summary>清除所有離線圖磚。</summary>
    public static void Clear()
    {
        try { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
        catch { /* 刪除失敗略過 */ }
    }
}
