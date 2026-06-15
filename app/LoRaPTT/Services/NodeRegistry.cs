using System.Collections.Concurrent;
using LoRaPTT.Models;
using Microsoft.Extensions.Logging;
// 避免與 MAUI 的 Microsoft.Maui.ApplicationModel.Communication.Contact 撞名
using Contact = LoRaPTT.Models.Contact;

namespace LoRaPTT.Services;

/// <summary>
/// 節點資料庫（NodeDB，F-036，對應韌體 nodedb）。
/// 訂閱 <see cref="IMessagingService"/> 事件，從收到的任何封包自動建檔，
/// 累積各節點的最後聽到時間 / RSSI / 暱稱 / 座標，供 UI 顯示節點清單與地圖。
/// 註冊為 Singleton 並於啟動時即建立，確保從開機就持續累積。
/// </summary>
public sealed class NodeRegistry
{
    private readonly IMessagingService _messaging;
    private readonly ILogger<NodeRegistry> _logger;

    // 以 Device ID 為鍵；收訊事件在背景執行緒觸發，故用執行緒安全字典
    private readonly ConcurrentDictionary<ushort, NodeInfo> _nodes = new();

    /// <summary>節點清單有變動時觸發（供 UI 重繪）</summary>
    public event Action? Changed;

    /// <summary>目前已知節點數</summary>
    public int Count => _nodes.Count;

    /// <summary>
    /// 建構子注入訊息服務並訂閱封包事件。
    /// </summary>
    /// <param name="messaging">訊息服務（封包來源）</param>
    /// <param name="logger">記錄器</param>
    public NodeRegistry(IMessagingService messaging, ILogger<NodeRegistry> logger)
    {
        _messaging = messaging;
        _logger = logger;

        _messaging.NodeHeard += OnNodeHeard;
        _messaging.PositionReceived += OnPositionReceived;
        _messaging.DeviceDiscovered += OnDeviceDiscovered;
    }

    /// <summary>
    /// 取得節點快照，依「最後聽到時間」由新到舊排序。
    /// </summary>
    /// <returns>節點清單複本（避免列舉時被背景更新）</returns>
    public IReadOnlyList<NodeInfo> GetNodes() =>
        _nodes.Values.OrderByDescending(n => n.LastSeen).ToList();

    // ── 事件處理 ────────────────────────────────────────────

    /// <summary>收到任何封包 → 更新該節點的最後聽到時間與 RSSI</summary>
    /// <param name="id">來源 Device ID</param>
    /// <param name="rssi">收訊 RSSI</param>
    private void OnNodeHeard(ushort id, short rssi)
    {
        var node = _nodes.GetOrAdd(id, key => new NodeInfo { DeviceId = key });
        node.LastSeen = DateTimeOffset.Now;
        node.Rssi = rssi;
        Changed?.Invoke();
    }

    /// <summary>收到定位封包 → 更新該節點座標</summary>
    /// <param name="id">來源 Device ID</param>
    /// <param name="lat">緯度</param>
    /// <param name="lon">經度</param>
    private void OnPositionReceived(ushort id, double lat, double lon)
    {
        var node = _nodes.GetOrAdd(id, key => new NodeInfo { DeviceId = key });
        node.Lat = lat;
        node.Lon = lon;
        node.HasPosition = true;
        Changed?.Invoke();
    }

    /// <summary>PING 探測回覆 → 更新該節點暱稱與最後聽到時間</summary>
    /// <param name="contact">探測發現的聯絡人</param>
    private void OnDeviceDiscovered(Contact contact)
    {
        var node = _nodes.GetOrAdd(contact.DeviceId, key => new NodeInfo { DeviceId = key });
        if (!string.IsNullOrEmpty(contact.Name))
            node.Name = contact.Name;
        node.LastSeen = DateTimeOffset.Now;
        Changed?.Invoke();
    }
}
