using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LoRaPTT.Models;
using LoRaPTT.Services;
using LoRaPTT.Services.Protocol;
using Microsoft.Extensions.Logging;
// 避免與 MAUI 的 Microsoft.Maui.ApplicationModel.Communication.Contact 撞名
using Contact = LoRaPTT.Models.Contact;

namespace LoRaPTT.ViewModels;

/// <summary>聊天頁 ViewModel：文字收發、聯絡人/群組、ACK 狀態、PING 探測</summary>
public partial class ChatViewModel : ObservableObject
{
    private readonly ICommService _comm;
    private readonly IMessagingService _messaging;
    private readonly RosterStore _store;
    private readonly ILogger<ChatViewModel> _logger;

    /// <summary>單則文字上限（UTF-8 bytes），等同封包 payload 上限</summary>
    public int DraftMaxBytes => PacketCodec.MaxPayload;

    // ── UI 綁定屬性 ────────────────────────────────────────
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _statusMessage = "未連線";
    [ObservableProperty] private string _draftText = "";
    [ObservableProperty] private string _targetHex = "FFFF";   // 預設廣播
    [ObservableProperty] private string _nickname = "LoRaPTT";
    [ObservableProperty] private bool _showManage;             // 管理面板開合
    [ObservableProperty] private string _newContactId = "";
    [ObservableProperty] private string _newContactName = "";
    [ObservableProperty] private string _newGroupName = "";

    /// <summary>目前草稿的 UTF-8 位元組數（F-013）</summary>
    public int DraftByteCount => Encoding.UTF8.GetByteCount(DraftText ?? "");

    /// <summary>草稿是否超過長度上限</summary>
    public bool DraftOverLimit => DraftByteCount > DraftMaxBytes;

    /// <summary>聊天訊息（依時間順序）</summary>
    public ObservableCollection<ChatMessage> Messages { get; } = new();

    /// <summary>已知/已發現的聯絡人</summary>
    public ObservableCollection<Contact> Contacts { get; } = new();

    /// <summary>群組清單</summary>
    public ObservableCollection<DeviceGroup> Groups { get; } = new();

    /// <summary>供 Blazor 頁面在資料變動時呼叫 StateHasChanged</summary>
    public event Action? StateChanged;

    public ChatViewModel(ICommService comm, IMessagingService messaging,
        RosterStore store, ILogger<ChatViewModel> logger)
    {
        _comm = comm;
        _messaging = messaging;
        _store = store;
        _logger = logger;

        _comm.OnConnectionChanged += OnConnectionChanged;
        _messaging.MessageReceived += OnMessageReceived;
        _messaging.AckReceived += OnAckReceived;
        _messaging.DeviceDiscovered += OnDeviceDiscovered;
        _messaging.HandshakeCompleted += OnHandshakeCompleted;

        // 載入持久化的暱稱/聯絡人/群組
        Nickname = _store.LoadNickname(_messaging.LocalNickname);
        _messaging.LocalNickname = Nickname;
        foreach (var c in _store.LoadContacts()) Contacts.Add(c);
        foreach (var g in _store.LoadGroups()) Groups.Add(g);
    }

    // 草稿變動 → 通知頁面更新字數
    partial void OnDraftTextChanged(string value) => Notify();

    // ── 連線 ────────────────────────────────────────────────
    [RelayCommand]
    private async Task ConnectAsync()
    {
        StatusMessage = "連線中...";
        Notify();
        try
        {
            await _comm.ConnectAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "連線失敗");
            StatusMessage = $"連線失敗：{ex.Message}";
            Notify();
        }
    }

    // ── 送出文字 ────────────────────────────────────────────
    [RelayCommand]
    private async Task SendAsync()
    {
        var text = DraftText?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        if (!IsConnected) { StatusMessage = "尚未連線"; Notify(); return; }
        if (Encoding.UTF8.GetByteCount(text) > DraftMaxBytes)   // F-013：事前擋下，不丟例外
        {
            StatusMessage = $"訊息過長（上限 {DraftMaxBytes} bytes）";
            Notify();
            return;
        }
        if (!TryParseTarget(out var dstId))
        {
            StatusMessage = "目標 ID 格式錯誤（請輸入 4 位十六進位，如 A001 或 FFFF）";
            Notify();
            return;
        }

        var msg = await _messaging.SendTextAsync(dstId, text);
        Messages.Add(msg);
        DraftText = "";
        Notify();
    }

    // ── 廣播 PING 探測 ──────────────────────────────────────
    [RelayCommand]
    private async Task PingAsync()
    {
        if (!IsConnected) { StatusMessage = "尚未連線"; Notify(); return; }
        try
        {
            await _messaging.SendPingAsync();
            StatusMessage = "已送出探測，等待附近裝置回覆...";
            Notify();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PING 探測失敗");
            StatusMessage = $"探測失敗：{ex.Message}";
            Notify();
        }
    }

    /// <summary>套用暱稱（影響 PING 回覆內容）並持久化</summary>
    partial void OnNicknameChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            _messaging.LocalNickname = value.Trim();
            _store.SaveNickname(value.Trim());
        }
    }

    // ── 事件處理 ────────────────────────────────────────────
    private void OnConnectionChanged(bool connected) => RunOnUi(() =>
    {
        IsConnected = connected;
        StatusMessage = connected ? "已連線，握手中..." : "已斷線";
    });

    private void OnHandshakeCompleted() => RunOnUi(() =>
    {
        var id = _messaging.LocalDeviceId;
        StatusMessage = id.HasValue
            ? $"已連線（本機 0x{id.Value:X4}，韌體 {_messaging.FirmwareVersion}）"
            : "已連線";
    });

    private void OnMessageReceived(ChatMessage msg) => RunOnUi(() =>
    {
        // F-022：群組訊息僅在「已加入該群組」時才顯示
        if (DstId.IsGroup(msg.DstId))
        {
            var g = Groups.FirstOrDefault(x => x.DstId == msg.DstId);
            if (g is null || !g.Joined) return;
        }
        Messages.Add(msg);
        TouchContact(msg.PeerId, msg.Rssi);
    });

    private void OnAckReceived(ushort responderId, ushort ackedSeq) => RunOnUi(() =>
    {
        // 找到對應的送出訊息，標記為已送達
        for (int i = Messages.Count - 1; i >= 0; i--)
        {
            var m = Messages[i];
            if (m.Direction == MessageDirection.Outgoing
                && m.Status == MessageStatus.Sending
                && m.Seq == ackedSeq)
            {
                m.Status = MessageStatus.Delivered;
                break;
            }
        }
    });

    private void OnDeviceDiscovered(Contact contact) => RunOnUi(() =>
    {
        var existing = Contacts.FirstOrDefault(c => c.DeviceId == contact.DeviceId);
        if (existing is null)
        {
            Contacts.Add(contact);
            StatusMessage = $"發現裝置：{contact.DisplayName}";
        }
        else
        {
            existing.Name = contact.Name;
            existing.LastSeen = contact.LastSeen;
        }
        _store.SaveContacts(Contacts);
    });

    // ── 聯絡人管理（F-003）──────────────────────────────────
    [RelayCommand]
    private void AddContact()
    {
        var t = NewContactId?.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(t)
            || !ushort.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id)
            || id == DstId.Reserved)
        {
            StatusMessage = "聯絡人 ID 格式錯誤（4 位十六進位）";
            Notify();
            return;
        }
        var existing = Contacts.FirstOrDefault(c => c.DeviceId == id);
        if (existing is null)
            Contacts.Add(new Contact { DeviceId = id, Name = NewContactName?.Trim() ?? "", Discovered = false });
        else
            existing.Name = NewContactName?.Trim() ?? existing.Name;
        _store.SaveContacts(Contacts);
        NewContactId = "";
        NewContactName = "";
        Notify();
    }

    // ── 群組管理（F-020~022）────────────────────────────────
    [RelayCommand]
    private void CreateGroup()
    {
        // 取最小未使用的群組索引（0~15）
        int idx = Enumerable.Range(0, DstId.GroupCount)
                            .FirstOrDefault(i => Groups.All(g => g.Index != i), -1);
        if (idx < 0)
        {
            StatusMessage = $"群組已達上限（{DstId.GroupCount} 組）";
            Notify();
            return;
        }
        Groups.Add(new DeviceGroup { Index = idx, Name = NewGroupName?.Trim() ?? "", Joined = true });
        _store.SaveGroups(Groups);
        NewGroupName = "";
        Notify();
    }

    [RelayCommand]
    private void ToggleGroupJoin(DeviceGroup group)
    {
        group.Joined = !group.Joined;
        _store.SaveGroups(Groups);
        Notify();
    }

    /// <summary>把指定位址設為傳送目標（聯絡人/群組點選用）</summary>
    [RelayCommand]
    private void SelectTarget(string idHex)
    {
        if (!string.IsNullOrEmpty(idHex)) { TargetHex = idHex; Notify(); }
    }

    // ── 輔助 ────────────────────────────────────────────────
    private void TouchContact(ushort id, int? rssi)
    {
        var c = Contacts.FirstOrDefault(x => x.DeviceId == id);
        if (c is null)
        {
            Contacts.Add(new Contact { DeviceId = id, LastSeen = DateTimeOffset.Now, LastRssi = rssi });
        }
        else
        {
            c.LastSeen = DateTimeOffset.Now;
            if (rssi.HasValue) c.LastRssi = rssi;
        }
    }

    private bool TryParseTarget(out ushort dstId)
    {
        dstId = DstId.Broadcast;
        var t = TargetHex?.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(t)) return false;
        if (!ushort.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            return false;
        if (v == DstId.Reserved) return false;
        dstId = v;
        return true;
    }

    /// <summary>在 UI 執行緒上執行並通知頁面更新</summary>
    private void RunOnUi(Action action)
    {
        if (Microsoft.Maui.ApplicationModel.MainThread.IsMainThread)
        {
            action();
            StateChanged?.Invoke();
        }
        else
        {
            Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() =>
            {
                action();
                StateChanged?.Invoke();
            });
        }
    }

    private void Notify() => StateChanged?.Invoke();
}
