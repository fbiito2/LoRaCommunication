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
    [ObservableProperty] private bool _isScanning;             // F-004：正在掃描附近裝置
    [ObservableProperty] private int _scanCountdown;           // 掃描倒數秒
    [ObservableProperty] private bool _showDiscoveryDialog;    // 探測結果對話框開合

    /// <summary>目前草稿的 UTF-8 位元組數（F-013）</summary>
    public int DraftByteCount => Encoding.UTF8.GetByteCount(DraftText ?? "");

    /// <summary>草稿是否超過長度上限</summary>
    public bool DraftOverLimit => DraftByteCount > DraftMaxBytes;

    /// <summary>聊天訊息（依時間順序）</summary>
    public ObservableCollection<ChatMessage> Messages { get; } = new();

    /// <summary>已知/已發現的聯絡人</summary>
    public ObservableCollection<Contact> Contacts { get; } = new();

    /// <summary>探測結果暫存（不持久化；由使用者在對話框挑選後才加入 Contacts）</summary>
    public ObservableCollection<Contact> DiscoveredDevices { get; } = new();

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
        _messaging.SosReceived += OnSosReceived; // A：收到 SOS 也寫進文字訊息

        // 載入持久化的暱稱/聯絡人/群組
        Nickname = _store.LoadNickname(_messaging.LocalNickname);
        _messaging.LocalNickname = Nickname;
        foreach (var c in _store.LoadContacts()) Contacts.Add(c);
        foreach (var g in _store.LoadGroups()) Groups.Add(g);
    }

    // 草稿變動 → 通知頁面更新字數
    partial void OnDraftTextChanged(string value) => Notify();

    // 目標變動 → 更新顯示名稱 & 廣播狀態
    partial void OnTargetHexChanged(string value) => Notify();

    /// <summary>目前是否為廣播模式（目標 FFFF）</summary>
    public bool IsBroadcastTarget
        => TargetHex?.Trim().Equals("FFFF", StringComparison.OrdinalIgnoreCase) ?? false;

    /// <summary>目前發送目標的友善顯示名稱</summary>
    public string TargetDisplayName
    {
        get
        {
            var t = TargetHex?.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(t) || t.Equals("FFFF", StringComparison.OrdinalIgnoreCase))
                return "所有人（廣播）";
            if (!ushort.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id))
                return "無效目標";
            if (DstId.IsGroup(id))
            {
                var g = Groups.FirstOrDefault(x => x.DstId == id);
                return g?.DisplayName ?? $"群組 0x{t}";
            }
            var c = Contacts.FirstOrDefault(x => x.DeviceId == id);
            return c != null ? c.DisplayName : $"裝置 0x{t}";
        }
    }

    /// <summary>從 store 重新讀取暱稱（跨頁同步用：在 Settings 改了暱稱，切回 Chat 時同步）</summary>
    private bool _syncingNickname;
    public void SyncNickname()
    {
        var saved = _store.LoadNickname("LoRaPTT");
        if (saved != Nickname)
        {
            _syncingNickname = true;
            Nickname = saved; // 透過 property 設定，觸發 UI 更新
            _messaging.LocalNickname = saved;
            _syncingNickname = false;
        }
    }

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

    // ── 中斷連線（讓使用者能斷線重連，解開「已連線卻不通」的卡死狀態）──
    [RelayCommand]
    private async Task DisconnectAsync()
    {
        try { await _comm.DisconnectAsync(); }
        catch (Exception ex) { _logger.LogError(ex, "中斷連線失敗"); }
        IsConnected = false;
        StatusMessage = "已中斷，可重新連線";
        Notify();
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

    // ── 廣播 PING 探測（F-004 增強版）─────────────────────
    [RelayCommand]
    private async Task PingAsync()
    {
        if (!IsConnected) { StatusMessage = "尚未連線"; Notify(); return; }
        try
        {
            DiscoveredDevices.Clear();    // 清掉上一輪探測結果
            ShowDiscoveryDialog = false;
            IsScanning = true;
            ScanCountdown = 5;
            Notify();

            StatusMessage = "正在搜尋附近裝置...";
            Notify();

            // 5 秒掃描窗內連發 3 次 PING（t≈0 / 1.5 / 3 秒）。
            // 半雙工下單一回覆偶爾會在碰撞中遺失（時有時無）；多發幾次讓對方有多次
            // 機會回覆（每次回覆各帶硬體亂數退避），收到任一次即可；重複回覆依
            // DeviceId 去重（OnDeviceDiscovered），清單只顯示一台。
            await _messaging.SendPingAsync();        // 第 1 次（t=0）
            for (int half = 1; half <= 10; half++)   // 10 × 0.5s = 5 秒
            {
                await Task.Delay(500);
                if (half == 3 || half == 6)          // ≈1.5s、3.0s 再各補一次
                    await _messaging.SendPingAsync();
                ScanCountdown = (10 - half + 1) / 2; // 近似剩餘秒
                Notify();
            }

            ScanCountdown = 0;
            IsScanning = false;
            // 探測完成 → 跳對話框讓使用者挑選要加入哪些裝置
            if (DiscoveredDevices.Count > 0)
            {
                ShowDiscoveryDialog = true;
                StatusMessage = $"探測完成，找到 {DiscoveredDevices.Count} 個裝置";
            }
            else
            {
                StatusMessage = "探測完成，未發現其他裝置";
            }
            Notify();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PING 探測失敗");
            StatusMessage = $"探測失敗：{ex.Message}";
            IsScanning = false;
            Notify();
        }
    }

    /// <summary>套用暱稱（影響 PING 回覆內容）並持久化</summary>
    partial void OnNicknameChanged(string value)
    {
        if (_syncingNickname) return; // 同步中不回寫，避免迴圈
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

    // A：收到 SOS（F-073）→ 在聊天室也插一則醒目訊息，含求救者 ID 與定位（有的話）
    // payload：[DeviceID 2B][Lat 8B][Lon 8B][附加文字 NB]；C6L 實體按鈕求救僅 2B(無 GPS)
    private void OnSosReceived(ushort srcId, byte[] payload) => RunOnUi(() =>
    {
        double lat = 0, lon = 0; string extra = "";
        if (payload.Length >= 18)
        {
            lat = BitConverter.ToDouble(payload, 2);
            lon = BitConverter.ToDouble(payload, 10);
            if (payload.Length > 18)
                extra = Encoding.UTF8.GetString(payload, 18, payload.Length - 18);
        }
        bool hasGps = lat != 0 || lon != 0;
        var text = $"🆘 SOS 緊急求救　來自 0x{srcId:X4}"
                 + (hasGps ? $"\n📍 定位：{lat:F6}, {lon:F6}" : "\n📍 無定位")
                 + (string.IsNullOrWhiteSpace(extra) ? "" : $"\n💬 {extra}");
        Messages.Add(new ChatMessage
        {
            Direction = MessageDirection.Incoming,
            PeerId = srcId,
            DstId = DstId.Broadcast,
            Text = text,
            Status = MessageStatus.Received,
        });
        TouchContact(srcId, null);
    });

    private void OnAckReceived(ushort responderId, ushort ackedSeq) => RunOnUi(() =>
    {
        // 找到對應的送出訊息，標記為已送達
        bool found = false;
        for (int i = Messages.Count - 1; i >= 0; i--)
        {
            var m = Messages[i];
            if (m.Direction == MessageDirection.Outgoing
                && m.Status == MessageStatus.Sending
                && m.Seq == ackedSeq)
            {
                m.Status = MessageStatus.Delivered;
                found = true;
                break;
            }
        }
        Console.WriteLine($"LPTT: OnAckReceived from=0x{responderId:X4} ackedSeq={ackedSeq} → {(found ? "配對成功(已送達)" : "找不到對應訊息")}");
    });

    private void OnDeviceDiscovered(Contact contact) => RunOnUi(() =>
    {
        // 排除本機自己（韌體已不彈回自身封包，此處再加一道保險）
        if (_messaging.LocalDeviceId.HasValue && contact.DeviceId == _messaging.LocalDeviceId.Value)
            return;

        // 放進「探測結果」暫存清單（不直接寫入持久化聯絡人，
        // 改由使用者在對話框中挑選要加入哪些）
        var existing = DiscoveredDevices.FirstOrDefault(c => c.DeviceId == contact.DeviceId);
        if (existing is null)
        {
            DiscoveredDevices.Add(contact);
        }
        else
        {
            existing.Name = contact.Name;
            existing.LastSeen = contact.LastSeen;
            existing.LastRssi = contact.LastRssi;
        }
        StatusMessage = $"✅ 探測到：{contact.DisplayName}";
    });

    /// <summary>探測結果中的裝置是否已是聯絡人</summary>
    public bool IsKnownContact(ushort id) => Contacts.Any(c => c.DeviceId == id);

    /// <summary>把探測到的裝置加入聯絡人（持久化）</summary>
    [RelayCommand]
    private void AddDiscovered(Contact contact)
    {
        if (contact is null) return;
        var existing = Contacts.FirstOrDefault(c => c.DeviceId == contact.DeviceId);
        if (existing is null)
        {
            Contacts.Add(new Contact
            {
                DeviceId = contact.DeviceId,
                Name = contact.Name,
                Discovered = true,
                LastSeen = contact.LastSeen,
                LastRssi = contact.LastRssi,
            });
        }
        else
        {
            existing.Name = contact.Name;
            existing.LastSeen = contact.LastSeen;
            existing.LastRssi = contact.LastRssi;
        }
        _store.SaveContacts(Contacts);
        StatusMessage = $"已新增聯絡人：{contact.DisplayName}";
        Notify();
    }

    /// <summary>關閉探測結果對話框</summary>
    [RelayCommand]
    private void CloseDiscoveryDialog()
    {
        ShowDiscoveryDialog = false;
        Notify();
    }

    /// <summary>移除聯絡人（F-003）</summary>
    [RelayCommand]
    private void RemoveContact(Contact contact)
    {
        if (contact is null) return;
        Contacts.Remove(contact);
        _store.SaveContacts(Contacts);
        StatusMessage = $"已移除聯絡人：{contact.DisplayName}";
        Notify();
    }

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
        // 排除本機自己（避免把自己加進聯絡人）
        if (_messaging.LocalDeviceId.HasValue && id == _messaging.LocalDeviceId.Value) return;
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
