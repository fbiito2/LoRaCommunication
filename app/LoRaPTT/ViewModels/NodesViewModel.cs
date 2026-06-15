using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LoRaPTT.Models;
using LoRaPTT.Services;
using Microsoft.Extensions.Logging;

namespace LoRaPTT.ViewModels;

/// <summary>
/// 節點清單頁 ViewModel（F-036）。顯示 NodeDB 累積的鄰居節點，
/// 並提供廣播 PING 探測以主動發現裝置。
/// </summary>
public partial class NodesViewModel : ObservableObject
{
    private readonly NodeRegistry _registry;
    private readonly IMessagingService _messaging;
    private readonly ILogger<NodesViewModel> _logger;

    /// <summary>供 Blazor 頁面在資料變動時呼叫 StateHasChanged</summary>
    public event Action? StateChanged;

    /// <summary>目前節點清單（依最後聽到由新到舊）</summary>
    public IReadOnlyList<NodeInfo> Nodes => _registry.GetNodes();

    /// <summary>已知節點數</summary>
    public int Count => _registry.Count;

    /// <summary>
    /// 建構子注入節點資料庫與訊息服務。
    /// </summary>
    /// <param name="registry">節點資料庫</param>
    /// <param name="messaging">訊息服務（送 PING 用）</param>
    /// <param name="logger">記錄器</param>
    public NodesViewModel(NodeRegistry registry, IMessagingService messaging,
        ILogger<NodesViewModel> logger)
    {
        _registry = registry;
        _messaging = messaging;
        _logger = logger;
        _registry.Changed += OnRegistryChanged;
    }

    /// <summary>送出廣播 PING 主動探測附近裝置（F-004）</summary>
    [RelayCommand]
    private async Task PingAsync()
    {
        try
        {
            await _messaging.SendPingAsync();
        }
        catch (Exception ex)
        {
            // 記錄錯誤，不中斷 UI
            _logger.LogError(ex, "送出 PING 探測失敗");
        }
    }

    /// <summary>
    /// 計算節點「多久前聽到」的友善字串。
    /// </summary>
    /// <param name="node">節點</param>
    /// <returns>如「5 秒前」「3 分前」「2 時前」</returns>
    public static string AgeText(NodeInfo node)
    {
        var sec = (int)(DateTimeOffset.Now - node.LastSeen).TotalSeconds;
        if (sec < 60) return $"{sec} 秒前";
        if (sec < 3600) return $"{sec / 60} 分前";
        return $"{sec / 3600} 時前";
    }

    /// <summary>節點資料庫變動 → 通知頁面重繪</summary>
    private void OnRegistryChanged() => StateChanged?.Invoke();
}
