# LoRa PTT 語音對講系統

## 協作規則

1. **每次對話開始前**：若與上次對話有一段時間間隔，必須先執行 `git pull` 確認是否有新的 commit，再開始工作
2. **每次更新完成後**：將變更 commit 並推上 git（`git push`）
3. **規格書**：完整功能規格見 `docs/SPEC.md`，開發時以規格書為準

## 專案目標

使用 M5Stack Unit C6L（ESP32-C6 + SX1262）模組，搭配自製手機 APP，實現多節點的文字與語音通訊系統。語音採用 PTT（Push-to-Talk）半雙工模式，所有節點同時具備端點通訊與洪泛中繼能力，適用於戶外活動、災難救援等無基礎網路設施的場景。

## 硬體規格

- **模組：** Unit C6L（M5Stack，型號 U202）
  - MCU: ESP32-C6（RISC-V 雙核，主核 160MHz + 低功耗核 20MHz）
  - LoRa: SX1262（發射功率 +22dBm，接收靈敏度 -147dBm）
  - 頻段: 868~923 MHz（台灣 ISM 使用 920-925 MHz）
  - WiFi 6（2.4GHz）+ BLE 5 + Zigbee 3.0
  - Flash: 16MB SPI Flash
  - 介面: USB Type-C（供電 5V + USB Serial CDC）
  - 擴展: Grove HY2.0-4P 接口（PORT.A）
    - GND（黑線）
    - 5V 電源輸入/輸出（紅線）
    - GPIO5（黃線，可作 I2C SCL / UART TX / 通用 GPIO）
    - GPIO4（白線，可作 I2C SDA / UART RX / 通用 GPIO）
  - 顯示: 0.66" OLED 單色螢幕（SSD1306, 64×48 解析度）
  - 互動: 使用者按鈕 × 1、蜂鳴器 × 1、WS2812C RGB LED × 1
  - 天線: RP-SMA 天線接口 × 2（2.4GHz WiFi 用 + LoRa 用，各一）
  - 尺寸: 62 × 24 × 8 mm
  - 功耗:
    - 睡眠（Grove 供電）: 696.86 μA
    - 睡眠（USB-C 供電）: 866.42 μA
    - LoRa RX 待機: ~85 mA
    - LoRa 最大功率連續發射: ~80 mA
  - 隨附: RP-SMA 天線 × 2（WiFi 3dBi + LoRa 3dBi）、Grove 連接線（20cm）
- **數量：** 至少 2 組（一端一個，中繼另加）
- **外接天線：** 如需增強收訊，直接更換 RP-SMA 接口的高增益天線（5dBi+）即可，不需轉接
- **手機：** Android / iOS，內建麥克風與喇叭，支援 WiFi
- **不需要擴展板** — C6L 已整合 WiFi + LoRa + USB-C + OLED + 按鈕，一條 USB-C 線即可

## 使用模式

**沒有需要切換的「使用模式」。** 所有節點硬體與韌體相同、中繼能力恆開；行為僅由「目前有哪些傳輸層連著 APP」決定。供電來源與「是否有 APP」彼此獨立。

C6L 提供兩種傳輸層連接手機，**可同時運作**（詳見「雙傳輸層通訊」）：

### USB Serial（Type-C 直連）

```
┌──────────┐
│   手機    │
│ 供電(+通訊)│
│   USB-C   │
│     │     │
│  ┌──────┐ │
│  │ C6L  │ │
│  └──────┘ │
└──────────┘
隨身帶著走
```

- USB-C 接手機，手機供電；**若該手機有 APP 並完成握手**則同時走 USB Serial（CDC）通訊
- 低延遲、穩定、不佔 WiFi
- 注意：USB 接上不等於有 APP——也可能只是借電（見下方電量共享場景）

### WiFi AP（手機無線連入）

```
行動電源/手機 ──USB-C供電──→ C6L ──WiFi AP──→ 手機（可在數十公尺外）
                              │
                            LoRa 收發
                            可架高處增強收訊
```

- C6L 開 WiFi AP 熱點（預設常開），手機連上後以 UDP 通訊
- 手機可離開數十公尺，C6L 可架高處增強 LoRa 收訊
- 適合定點/營地/車上

### 電量共享場景（兩傳輸層同時用）

A 手機有 APP 但電量低、B 手機有電但沒 APP：C6L 用 Type-C 接 B 手機純供電，**同時**開 WiFi AP 讓 A 手機連入傳訊。兩傳輸層缺一不可，證明「USB／WiFi 二選一」是錯的。

### 純中繼

C6L 只被供電、**沒有任何 APP 連線（USB 無握手、WiFi 無 client）** → 自動成為純中繼站，架在任何位置都只做洪泛轉發。

## 系統架構

### 直連模式

```
手機A（錄音/播放） ←USB-C→ C6L(A) ←LoRa 920MHz→ C6L(B) ←USB-C→ 手機B（錄音/播放）
```

### 中繼模式（超出直連範圍時）

```
手機A ←USB-C→ C6L(A) ←LoRa→ C6L(R) 中繼 ←LoRa→ C6L(B) ←USB-C→ 手機B
                                │
                            行動電源供電
                            純轉發封包
```

### 群組通話模式（節點同時通訊+中繼）

```
手機A ←USB-C→ C6L(A) ←──LoRa──→ C6L(B) ←──LoRa──→ C6L(C) ←USB-C→ 手機C
                                    ↑
                                USB-C 或 WiFi
                                    ↓
                                  手機B（同時通話 + 中繼）
```

- C6L 作為 USB Serial/WiFi ↔ LoRa 透傳橋接器（bridge）
- 手機與 C6L 的連線方式：USB Serial（CDC）與 WiFi UDP，兩者可同時運作
- 所有音訊處理（錄音、編碼、解碼、播放）在手機端完成
- 不需要外接麥克風或喇叭模組
- 每個節點可同時當中繼，也可部署中繼專用節點

## 技術決策

### 語音編碼：Codec2 @ 2400bps

- **選擇原因：** LoRa 有效資料速率僅 0.3~27 kbps，只有 Codec2 能壓到 LoRa 可承受的位元率
- **參數：** 2400bps，每幀 20ms / 6 bytes
- **封包策略：** 累積 200ms（10 幀）送一包，約 60 bytes payload
- **預估延遲：** 0.5~1 秒（可接受的對講機體驗）
- Codec2 官方倉庫: https://github.com/drowe67/codec2

### 通訊模式：PTT 半雙工

- 按住按鈕說話，放開接收
- 一次只有一端在傳送
- 簡化 LoRa 收發邏輯，避免碰撞

### 通訊資料量評估

| 項目 | 數值 |
|------|------|
| Codec2 每秒資料量 | 300 bytes |
| 每包 payload（200ms） | ~60 bytes |
| 封包 overhead（header 8B + MAC 4B） | +12 bytes |
| 加密後每包總大小 | ~72 bytes |
| USB Serial 單次傳輸 | 無實質限制 ✅ |
| WiFi UDP 單包上限 | ~65507 bytes ✅ |
| LoRa 單包上限 | ~255 bytes ✅ |

### 雙傳輸層通訊：USB Serial + WiFi（可同時）

手機與 C6L 之間的兩種傳輸層**可同時運作**，不是二選一、也沒有模式切換。LoRa 收到的封包推送給**所有已握手的傳輸層**，任一傳輸層送來的資料都會發 LoRa。

**韌體連線判定邏輯：**

```
開機
  → WiFi AP 預設啟動（可由按鈕長按 3 秒關閉省電）
  → USB CDC：偵測到 USB host（DTR）→ 開啟 CDC，但「僅供電」與「有 APP」要再區分
  → 任一傳輸層收到 APP 的握手封包（F-053）→ 標記該傳輸層「有 APP」、開始雙向橋接
  → 沒有任何傳輸層握手 → 純中繼站（只做 LoRa 洪泛轉發）
```

> **關鍵：USB host 接上 ≠ 有 APP。** 純供電的手機（如借電的 B 手機）有 DTR 但不會送握手，C6L 不把它當資料端。必須靠 F-053 握手區分。

**USB Serial（CDC）：**
- 透過 USB-C 直接以 Serial 通訊，同時由手機供電
- 延遲極低、最穩定、不佔無線頻寬
- Android 手機需 USB OTG 支援（絕大多數都有）
- APP 端使用 USB Serial library 讀寫

**WiFi AP + UDP：**
- C6L 開 WiFi 熱點（SSID 如 `LoRaPTT_A01B`，密碼可透過 APP 設定），預設常開
- 手機連上該 WiFi 後以 UDP 傳輸資料（port 5000）
- 封包格式與 USB Serial 完全一致，只是傳輸層不同
- 不需外部路由器，野外也能用

**為什麼語音用 UDP 而非 TCP：**

| | UDP | TCP / WebSocket |
|---|---|---|
| 延遲 | 低（無重傳機制） | 高（等待重傳） |
| 丟包處理 | 丟了就算（語音可容忍） | 會卡住等重傳 |
| 適合語音 | ✅ | ❌ |

**韌體通訊抽象介面（C++ 端）：**

USB Serial 和 WiFi 共用同一個介面，LoRa handler 不需知道上層用哪種連線：

```cpp
// comm_interface.h — 通訊抽象介面
class ICommInterface {
public:
    virtual void begin() = 0;
    virtual void send(const uint8_t* data, size_t len) = 0;
    virtual void onReceive(std::function<void(const uint8_t*, size_t)> callback) = 0;
    virtual bool isConnected() = 0;
    virtual ~ICommInterface() = default;
};
```

### WiFi AP 設定流程

C6L 是自己開熱點（AP），不是去連別人的 WiFi，所以**不需要在 C6L 上輸入密碼或選 WiFi**。

**預設值（韌體出廠設定）：**
- SSID: `LoRaPTT_{DEVICE_ID}`（如 `LoRaPTT_A01B`）
- 密碼: 預設固定值（如 `loraptt2026`）
- UDP Port: 5000

**修改設定的方式：**

1. **透過 APP（主要方式）：** APP（USB 或 WiFi 任一已握手傳輸層）送設定指令，C6L 儲存到 Flash（NVS）
2. **透過 OLED + 按鈕（輔助方式）：** 韌體提供簡易選單，可查看/切換基本設定
3. 設定一次存入 ESP32-C6 的 NVS（Non-Volatile Storage），斷電不遺失

**APP 設定指令格式（透過 USB Serial 傳送）：**

```
{"cmd":"set_config","wifi_ssid":"LoRaPTT_A","wifi_pass":"mypassword","device_name":"Node_A","lora_freq":920}
```

C6L 收到後寫入 NVS，回應 `{"status":"ok"}`，下次開機生效。

### OLED 顯示與按鈕互動

C6L 內建 0.66" OLED（SSD1306, 64×48）和一顆使用者按鈕，韌體實作簡易 HMI：

**顯示頁面（短按按鈕切換）：**

```
頁面1：ID/暱稱      頁面2：網路         頁面3：LoRa         頁面4：中繼
┌────────────┐    ┌────────────┐    ┌────────────┐    ┌────────────┐
│ ID:A01B    │    │ WiFi: ON   │    │ Freq:920M  │    │ Relay:42   │
│ Name:NodeA │    │ LoRaPTT_   │    │ SF:7 BW500 │    │ Rx:120     │
│ APP:USB+WiFi│   │ A01B       │    │ RSSI:-87   │    │ Tx:35      │
│ ▓▓▓▓░ Batt │    │ IP:192.4.1 │    │ TX:+22dBm  │    │ RSSI:-87   │
└────────────┘    └────────────┘    └────────────┘    └────────────┘
```

**按鈕操作：**
- 短按：切換顯示頁面
- 長按 3 秒：開/關 WiFi AP（省電；切換後 RGB LED 變色提示 + 蜂鳴器嗶一聲）
- 長按 6 秒：重置設定為出廠預設值

**RGB LED 狀態指示：**
- 綠色常亮：USB 有 APP 已握手連線
- 藍色常亮：WiFi AP 已啟動且有手機連入
- 藍色閃爍：WiFi AP 已啟動，等待手機連入
- 紅色閃爍：LoRa 發送中
- 黃色閃爍：LoRa 接收中
- 紅色常亮：錯誤狀態

## 安全機制

### Phase 1 — 不加密

災難救援場景優先確保通訊可靠性，Phase 1 不實作加密：
- 降低系統複雜度與除錯難度
- 中繼節點可直接轉發，不需金鑰
- MAC 欄位先填 CRC32 做基本完整性檢查

### Phase 2（後期）— 可選加密

未來有需求時加入 AES-128-CTR + HMAC-SHA256，作為可選功能：

| 威脅 | 說明 | 對策 |
|------|------|------|
| 竊聽 | 第三方用 SDR 或同款 LoRa 模組直接收聽 | AES-128-CTR 加密 payload |
| 偽冒 | 偽造封包假裝是合法裝置 | HMAC-SHA256 訊息驗證碼 |
| 重放攻擊 | 錄下封包重新播放 | 遞增序號（nonce），丟棄舊序號 |

- **AES-128-CTR：** 串流加密模式，適合語音連續資料，不需 padding，加解密邏輯相同
- **HMAC-SHA256（截斷 4 bytes）：** 驗證封包完整性與來源身份
- **Pre-Shared Key（PSK）：** 預先燒入相同金鑰（16 bytes AES key + 32 bytes HMAC key）
- **加密庫：** ESP-IDF 內建 mbedtls，不需額外安裝

### LoRa 封包格式

```
┌──────────────────────── 明文區 ────────────────────────────┐┌── 加密區 ──┐┌─ 驗證 ─┐
│ SRC_ID (2B) │ DST_ID (2B) │ HOP (1B) │ SEQ (2B) │ TYPE (1B) │ PAYLOAD (NB) │ MAC (4B) │
└────────────────────────────────────────────────────────────┘└─────────────┘└─────────┘
```

| 欄位 | 大小 | 說明 |
|------|------|------|
| SRC_ID | 2 bytes | 原始發送者裝置 ID |
| DST_ID | 2 bytes | 目標接收者 ID（0x0001~0xFFFE=點對點；0xFFFF=廣播；0xFFE0~0xFFEF=群組，最多 16 組） |
| HOP | 1 byte | 剩餘跳數，每次中繼轉發減 1，歸 0 丟棄（依類型：文字=5、SOS=15、語音=3） |
| SEQ | 2 bytes | 遞增封包序號，作為 AES-CTR nonce 一部分，防重放 |
| TYPE | 1 byte | 0x01=文字, 0x02=語音, 0x03=控制/心跳, 0x04=ACK, 0x05=PING/探測, 0x06=SOS |
| PAYLOAD | N bytes | AES-128-CTR 加密後的資料（文字或 Codec2 語音幀） |
| MAC | 4 bytes | HMAC-SHA256 截斷，對整個封包（含明文區+加密區）計算 |

### AES-CTR Nonce 組成（16 bytes）

```
[SRC_ID 2B][SEQ 2B][0x00 填充 12B]
```

### 韌體加解密流程

**發送端：**
1. 組合明文 header（SRC_ID + DST_ID + HOP + SEQ + TYPE）
2. 用 AES-128-CTR 加密 payload（nonce = SRC_ID + SEQ + padding）
3. 對「明文 header + 加密 payload」計算 HMAC-SHA256，截斷取前 4 bytes 作為 MAC
4. 組合完整封包發送，SEQ++

**接收端：**
1. 解析明文 header，取得 SRC_ID、DST_ID、HOP、SEQ
2. 檢查 SRC_ID 是否為已知裝置（白名單）
3. 檢查 SEQ 是否大於該 SRC_ID 上次收到的序號（防重放）
4. 驗證 MAC：對「明文 header + 加密 payload」計算 HMAC，比對 MAC 欄位
5. MAC 驗證通過 → 判斷 DST_ID：
   - 是自己或廣播 → AES-128-CTR 解密 payload → 交給手機（USB Serial 或 WiFi）
   - 不是自己且 HOP > 0 → 進入中繼轉發流程（見 Mesh Relay 章節）
6. 更新該 SRC_ID 的最後序號記錄

### 金鑰管理

- **初始版本：** PSK 寫死在韌體中（兩台燒同一組），適合個人使用
- **未來擴展：** 可透過 USB Serial 或 WiFi 安全通道從 APP 下發金鑰，支援換鑰

## 電源管理

### SX1262 RxDutyCycle 省電機制

SX1262 內建硬體級 RxDutyCycle 模式（CAD + Sleep 輪替），不需 CPU 持續運行：

```
┌──────┐  ┌──────┐  ┌──────┐  ┌──────┐
│ 偵聽  │  │ 睡眠  │  │ 偵聽  │  │ 睡眠  │ ...
│ ~5ms │  │~995ms│  │ ~5ms │  │~995ms│
└──────┘  └──────┘  └──────┘  └──────┘
```

SX1262 硬體自主計時醒來，偵測 LoRa preamble（前導碼），無訊號則繼續睡眠，偵測到訊號才透過 DIO1 中斷喚醒 ESP32-C6。

### 耗電比較

| 模式 | SX1262 電流 | 說明 |
|------|------------|------|
| 連續接收（RX） | ~4.5 mA | 一直開著聽，最耗電 |
| RxDutyCycle（1 秒週期） | ~0.05 mA 平均 | 每秒醒 5ms 偵測，省電 99% |
| 深度睡眠 | ~0.0006 mA | 完全關閉，不收訊號 |

### 延遲 vs 省電取捨

發送端需加長 preamble，確保接收端在睡眠週期內能偵測到：

| 睡眠週期 | 偵測延遲（最大） | 省電效果 | 適用場景 |
|---------|----------------|---------|---------|
| 500ms | 0.5 秒 | 中 | 即時性要求較高 |
| 1 秒 | 1 秒 | 高 | ✅ PTT 對講（建議值） |
| 5 秒 | 5 秒 | 極高 | 純文字訊息 |

### 狀態機設計

韌體根據使用狀態自動切換功耗模式：

```
                  ┌──────────────────────────────────────────┐
                  ↓                                          │
待機模式（省電）──收到封包或按PTT──→ 通話模式（全速）──放開+超時3秒──┘
```

**待機模式（等待來訊）：**
- SX1262：RxDutyCycle，週期 1 秒
- ESP32-C6：light sleep，由 SX1262 DIO1 中斷喚醒
- 整機平均功耗極低

**通話模式（PTT 進行中）：**
- SX1262：連續 RX（即時收語音封包，不能漏幀）
- ESP32-C6：全速運行，處理加解密與資料轉發
- 通話結束後（放開 PTT + 3 秒無封包）→ 自動退回待機模式

### Preamble 策略

- 待機模式下，發送端第一個封包使用加長 preamble（覆蓋接收端的睡眠週期）
- 進入通話模式後，後續語音封包使用正常長度 preamble（接收端已切為連續 RX）
- 這樣只有第一個封包有額外延遲，後續語音串流不受影響

## Mesh Relay（中繼轉發）

### 概念

當兩端距離超出 LoRa 單跳傳輸範圍，可在中間放置一或多台 C6L 作為中繼節點，自動轉發封包：

```
手機A ←USB-C→ C6L(A) ←LoRa→ C6L(R) 中繼 ←LoRa→ C6L(B) ←USB-C→ 手機B
                              │
                          不接手機
                          純轉發封包
                          USB 長期供電
```

中繼節點不需要接手機、不需要解密 payload，只做封包級轉發。

### 中繼節點處理邏輯

```
收到 LoRa 封包
  → 驗證 MAC（確認封包完整性，防止轉發垃圾封包）
  → 檢查 SRC_ID + SEQ 是否已轉發過（去重，防止迴圈）
  → DST_ID 是自己？→ 交給手機處理
  → DST_ID 不是自己（含廣播 0xFFFF）？
      → HOP > 0？→ HOP-- → 重新 LoRa 發送（轉發）
      → HOP == 0？→ 丟棄
```

### 每個節點都是端點 + 中繼

所有節點同時具備收發和轉發能力，不需切換模式：

```
收到 LoRa 封包
  → 去重檢查通過
  → DST_ID 是自己或廣播或所屬群組？→ 處理（交給手機 APP）
  → 同時 HOP > 0 且 SRC_ID 不是自己？→ HOP-- → LoRa 轉發
  → 兩步驟同時進行，先處理再轉發
```

### 去重機制

每個節點維護一個 **已見封包快取**（ring buffer），記錄最近收到的 `SRC_ID + SEQ` 組合：

- 收到封包先查快取，已存在則丟棄（避免重複處理和迴圈轉發）
- 快取大小建議 64~128 筆，FIFO 覆蓋最舊的

### 語音中繼延遲評估

| 路徑 | 延遲估算 | 語音體驗 |
|------|---------|---------|
| 直連（A→B） | 0.5~1 秒 | 良好 |
| 一跳中繼（A→R→B） | 1~2 秒 | 可接受 |
| 兩跳中繼（A→R1→R2→B） | 1.5~3 秒 | 勉強 |
| 三跳以上 | >3 秒 | 語音不建議，文字仍可 |

預設 MAX_HOP = 3，足夠大多數場景。

### 節點運作差異

所有節點硬體與韌體完全相同，差異僅在是否有 APP 握手連線：

| 項目 | 有 APP 連線 | 無 APP 連線（無人中繼站） |
|------|-----------|----------------------|
| 端點功能 | 收發自己的訊息 | 無（無 APP 處理） |
| 中繼功能 | 同時轉發他人封包 | 只做轉發 |
| 手機通訊 | USB Serial 和/或 WiFi（已握手者） | 無任何傳輸層握手（USB 僅供電或未接、WiFi 無 client） |
| 供電方式 | USB-C 接手機/行動電源 | Grove 5V / USB-C 接行動電源或太陽能 |
| OLED 顯示 | 完整狀態 | 中繼統計（轉發封包數、RSSI） |

### 部署建議

- 中繼節點放制高點（屋頂、山頂、高樓），搭配高增益全向天線（5dBi+）效果最佳
- 多個中繼節點可形成 mesh 網路，封包自動沿可達路徑轉發
- 城市環境建議每 1~3 km 一個中繼節點
- 空曠地形單跳可達 5~15 km，中繼需求較低

### 中繼節點 Grove 供電部署方案

中繼節點建議使用 Grove 接口供電，睡眠功耗比 USB-C 更低（696μA vs 866μA），適合長期無人值守：

**方案 A — 太陽能自給自足（戶外長期部署）：**

```
太陽能板（5V）→ 充電控制器 → 18650 鋰電池
                                    │
                              Grove 5V + GND
                                    │
                                 C6L（中繼）
                                    │
                              RP-SMA 高增益天線
                                    │
                              架在屋頂/山上
```

**方案 B — 行動電源簡易部署（臨時中繼）：**

```
行動電源 ──Grove 轉接線──→ C6L（中繼）
           5V + GND
```

**Grove 供電接線方式：**
- 紅線 → 5V（電源正極）
- 黑線 → GND（接地）
- 黃線（G5）、白線（G4）→ 不接（中繼節點不需要）

如需同時接感測器（如溫濕度、GPS），G4/G5 可作 I2C 使用，中繼節點順便回傳環境資料。

### Grove 接口擴展應用（選配）

Grove 的 G4（I2C SDA）/ G5（I2C SCL）可接 M5Stack 生態系感測器模組：

| 感測器模組 | 用途 | 適用場景 |
|-----------|------|---------|
| ENV III（SHT30 + QMP6988） | 溫度、濕度、氣壓 | 中繼節點兼氣象站 |
| GPS 模組（UART） | 位置資訊 | 移動節點回報座標 |
| PIR 人體感測 | 移動偵測 | 遠端監控警報 |
| Light Sensor | 環境光線 | 太陽能充電狀態監測 |

感測器資料可透過 LoRa 封包（TYPE=0x04 感測器資料）夾帶回傳。

### 廣播群組通話（通訊+中繼同時運作）

一般節點可以同時作為通話端和中繼端。典型場景：A、B、C 三點通訊，A-C 距離過遠無法直連，B 同時通話並中繼。

```
手機A ←USB-C→ C6L(A) ←──LoRa──→ C6L(B) ←──LoRa──→ C6L(C) ←USB-C→ 手機C
                                  ↑
                              USB-C 或 WiFi
                                  ↓
                                手機B（B 同時通話 + 中繼）
```

**使用廣播模式（DST_ID = 0xFFFF）** 實現群組通話，每個節點收到封包後同時做兩件事：

```
收到 LoRa 封包，DST_ID = 0xFFFF（廣播）
  → MAC 驗證通過 + SRC_ID 不是自己 + 去重檢查通過
  → ① 自己處理：解密 payload → 推送 → 手機播放
  → ② 同時轉發：HOP > 0？→ HOP-- → LoRa 重發（不需解密 payload）
  → 兩步驟順序執行，先處理再轉發（確保本地播放不被轉發延遲影響）
```

**各節點發話時的封包路徑：**

| 發話者 | A 收到方式 | B 收到方式 | C 收到方式 |
|--------|-----------|-----------|-----------|
| A 說話 | — | LoRa 直收 | B 中繼轉發 |
| B 說話 | LoRa 直收 | — | LoRa 直收 |
| C 說話 | B 中繼轉發 | LoRa 直收 | — |

**SX1262 半雙工限制：**

SX1262 同一瞬間只能 RX 或 TX。B 在轉發 A 的封包期間如果 C 也在發送，B 會漏掉 C 的那個封包。處理方式：

- 語音是連續封包流（每 200ms 一包），偶爾丟一兩包影響不大
- Codec2 解碼端遇到缺失幀時做靜音填充（插入靜音幀或重複上一幀）
- 轉發耗時很短（封包小，發送約幾十 ms），碰撞機率低
- PTT 模式下同一時間通常只有一人在說話，進一步降低碰撞

**點對點 vs 廣播模式對照：**

| | 點對點（DST_ID = 特定 ID） | 廣播（DST_ID = 0xFFFF） |
|---|---|---|
| 用途 | 私人一對一通話 | 群組通話 |
| 中繼節點行為 | 只轉發不播放 | 播放 + 轉發 |
| 適用場景 | 兩人私聊 | 多人對講群組 |

APP 端可讓使用者選擇通話模式：群組廣播或指定對象。

## 韌體（C6L 端）

### 框架：Arduino + ESP-IDF

- **功能：** USB Serial + WiFi ↔ LoRa 雙向透傳，**兩傳輸層可同時橋接**（無模式切換）
- **USB Serial（CDC）：** 透過 USB-C 直接與手機通訊，延遲最低（需 APP 握手後啟用為資料通道）
- **WiFi AP + UDP：** WiFi 熱點 + UDP Server（port 5000），預設常開，手機連上後以 UDP 收發
- **連線握手（F-053）：** 任一傳輸層收到 APP hello 才視為「有 APP」並雙向橋接；純供電不送資料
- **通訊抽象：** USB Serial 和 WiFi 實作共同的 ICommInterface；可同時掛多個傳輸層（多工），LoRa handler 不需知道上層連線方式
- **LoRa：** 使用 SX1262 驅動，收到手機資料就 LoRa 發送，收到 LoRa 資料就推給所有已握手傳輸層
- **HMI：** OLED 顯示狀態 + 按鈕切頁/WiFi 開關 + RGB LED 狀態指示 + 蜂鳴器提示
- **設定：** 透過 USB/WiFi 接收 JSON 設定指令（與資料封包分流），儲存到 NVS
- **不使用 Meshtastic 韌體**（Meshtastic 不支援語音串流，自寫精簡韌體延遲更低）

### PlatformIO 配置參考

```ini
[env:m5stack-unitc6l]
platform = https://github.com/pioarduino/platform-espressif32/archive/refs/heads/develop.zip
board = esp32-c6-devkitc-1
framework = arduino
upload_speed = 1500000
monitor_speed = 115200
build_flags =
    -D ARDUINO_USB_MODE=1
    -DARDUINO_USB_CDC_ON_BOOT=1
lib_deps =
    M5Unified=https://github.com/m5stack/M5Unified
```

注意：刷韌體時需按住側邊 Reset 按鈕 3 秒直到綠燈變紅，進入下載模式。

### 韌體目錄結構

```
firmware/
├── platformio.ini
├── src/
│   ├── main.cpp                   # 主程式：初始化各傳輸層（USB+WiFi 多工）+ 各模組
│   ├── comm_interface.h           # 通訊抽象介面（ICommInterface）
│   ├── usb_serial_service.h/.cpp  # USB Serial CDC（實作 ICommInterface）
│   ├── wifi_service.h/.cpp        # WiFi AP + UDP Server（實作 ICommInterface）
│   ├── lora_handler.h/.cpp        # SX1262 LoRa 收發
│   ├── crypto.h/.cpp              # AES-128-CTR 加解密 + HMAC 驗證（mbedtls）
│   ├── packet.h/.cpp              # 封包格式定義、組包/解包（含 SRC_ID/DST_ID/HOP）
│   ├── relay.h/.cpp               # 中繼轉發邏輯、去重快取（ring buffer）
│   ├── power_mgr.h/.cpp           # 電源狀態機：待機↔通話模式切換、RxDutyCycle 控制
│   ├── display.h/.cpp             # OLED SSD1306 顯示（狀態頁面、設定頁面）
│   ├── button.h/.cpp              # 按鈕處理（短按切頁、長按切模式、超長按重置）
│   ├── led.h/.cpp                 # WS2812C RGB LED 狀態指示
│   └── config.h/.cpp              # NVS 設定管理（WiFi SSID/密碼、裝置名稱、LoRa 參數）
└── lib/
```

## APP（手機端）

### 框架：.NET MAUI Blazor Hybrid

- 支援 Android + iOS
- USB Serial 通訊使用 UsbSerialForAndroid（NuGet），iOS 用 ExternalAccessory
- WiFi UDP 通訊使用 `System.Net.Sockets.UdpClient`

### 通訊抽象介面

USB Serial 和 WiFi 實作同一個 `ICommService` 介面，APP 不需關心底層連線方式：

```csharp
/// <summary>
/// 通訊服務介面 — USB Serial 與 WiFi 共用
/// </summary>
public interface ICommService
{
    /// <summary>連線到 C6L 裝置</summary>
    Task ConnectAsync(CancellationToken ct);

    /// <summary>中斷連線</summary>
    Task DisconnectAsync();

    /// <summary>發送資料到 C6L</summary>
    Task SendAsync(byte[] data, CancellationToken ct);

    /// <summary>收到 C6L 資料時觸發</summary>
    event Action<byte[]> OnDataReceived;

    /// <summary>目前是否已連線</summary>
    bool IsConnected { get; }

    /// <summary>目前通訊模式</summary>
    CommMode Mode { get; }
}

/// <summary>通訊傳輸層列舉</summary>
public enum CommMode
{
    /// <summary>USB Serial（CDC）連線</summary>
    UsbSerial,
    /// <summary>WiFi UDP 連線</summary>
    WiFi
}
```

### APP 模組

1. **通訊模組（雙傳輸層）**
   - `UsbSerialCommService`：偵測 USB-C 連接的 C6L、Serial 讀寫
   - `WiFiCommService`：連接 C6L WiFi AP、UDP 收發
   - APP 連上後送握手（F-053）；可偵測 USB 裝置和 WiFi SSID，讓使用者選擇連線方式

2. **音訊錄製模組（平台原生）**
   - Android: `AudioRecord`（PCM 16-bit, 8000Hz, Mono）
   - iOS: `AVAudioEngine`
   - 透過 MAUI platform-specific code 呼叫

3. **音訊播放模組（平台原生）**
   - Android: `AudioTrack`
   - iOS: `AVAudioEngine`
   - 接收 → Codec2 解碼 → PCM → 喇叭播放

4. **Codec2 編解碼模組**
   - 將 Codec2 C 原始碼編譯為 native library（.so / .dylib）
   - MAUI 用 P/Invoke 呼叫
   - 錄音 PCM → Codec2 encode → 透過 ICommService 傳送
   - ICommService 接收 → Codec2 decode → PCM 播放

### APP 目錄結構

```
app/
├── LoRaPTT.sln
├── LoRaPTT/
│   ├── MauiProgram.cs
│   ├── Services/
│   │   ├── ICommService.cs           # 通訊抽象介面（USB Serial / WiFi 共用）
│   │   ├── UsbSerialCommService.cs   # USB Serial CDC 實作
│   │   ├── WiFiCommService.cs        # WiFi UDP 實作（使用 UdpClient）
│   │   ├── AudioRecordService.cs     # 平台錄音抽象
│   │   ├── AudioPlayService.cs       # 平台播放抽象
│   │   └── Codec2Service.cs          # P/Invoke Codec2
│   ├── ViewModels/
│   │   └── MainViewModel.cs          # PTT 狀態、連線狀態、模式切換
│   ├── Pages/
│   │   └── MainPage.razor            # PTT 按鈕 UI、連線模式選擇
│   └── Platforms/
│       ├── Android/
│       │   ├── AudioRecordImpl.cs
│       │   ├── AudioPlayImpl.cs
│       │   └── UsbSerialImpl.cs      # Android USB OTG Serial 實作
│       └── iOS/
│           ├── AudioRecordImpl.cs
│           └── AudioPlayImpl.cs
└── libs/
    └── codec2/                       # Codec2 native libraries
        ├── android/
        │   ├── arm64-v8a/libcodec2.so
        │   └── x86_64/libcodec2.so
        └── ios/
            └── libcodec2.a
```

## 開發順序

### Phase 1：驗證硬體
- 刷 Meshtastic 韌體到兩組 C6L
- 用 Meshtastic 官方 App 確認 LoRa 通訊正常
- 確認通訊連線穩定（USB Serial 或 WiFi）

### Phase 2：USB Serial/WiFi ↔ LoRa 透傳韌體
- 寫 Arduino 韌體，實現 USB Serial（CDC）通訊
- 實現 LoRa 收發
- 用手機 Serial 終端工具或自製測試 APP 測試雙向透傳

### Phase 3：APP 文字版
- MAUI Blazor Hybrid 專案建置
- 整合 USB Serial library，連接 C6L
- 實現文字訊息收發

### Phase 4：PTT 語音
- 編譯 Codec2 native library
- 整合平台音訊錄製與播放
- 實現 PTT 按鈕邏輯
- 端到端語音通訊測試

### Phase 5：Mesh Relay 中繼完善
- 韌體完善洪泛中繼轉發邏輯與去重快取
- 每台節點同時是端點 + 中繼，無需模式切換
- 部署無人中繼站（C6L + 行動電源/太陽能）測試
- 多跳轉發的延遲與穩定性驗證

### 後期功能
- AES-128 加密（可選，PSK 機制）
- 韌體 OTA 更新（APP 透過 WiFi 推送，OLED 顯示進度）
- Grove 感測器擴展（溫濕度/GPS 等）
- 長訊息分包/組包
- 離線訊息暫存

## Coding Conventions

- **語言：** C#（.NET 8），韌體用 C++（Arduino）
- **命名：** 變數 lowerCamelCase、方法 UpperCamelCase、常數 ALL_CAPS、private fields 加 `_` 前綴
- **註解：** 全部使用繁體中文
- **非同步：** 所有 I/O 使用 async/await，不使用 .Result/.Wait()，CancellationToken 傳遞到最底層
- **錯誤處理：** 不允許空 catch、使用 ILogger、錯誤訊息繁體中文
- **USB Serial 相關：** Android 使用 UsbSerialForAndroid NuGet 套件
- **UI：** Blazor Razor Pages + Bootstrap 5

## 注意事項

- 台灣 ISM 頻段 920-925 MHz 有 duty cycle 限制，語音串流需注意合規
- USB Serial（CDC）需 Android 手機支援 USB OTG，絕大多數手機都有
- iOS 的 USB Serial 支援較受限，可能需走 WiFi 模式為主
- Codec2 的 P/Invoke 需要分別為 Android（arm64/x86_64）和 iOS 編譯
- LoRa 速率與距離互斥：SF 越低速率越高但距離越短，語音需要用 SF7 + BW500kHz 的高速設定
- 中繼節點需防止封包迴圈（去重快取 + HOP 遞減歸零丟棄）
- 中繼節點不解密 payload，只驗 MAC 後轉發，降低延遲
- 中繼節點接行動電源放高處搭配高增益天線（RP-SMA 直接更換 5dBi+ 天線）可大幅擴展覆蓋範圍
- 中繼節點建議使用 Grove 接口供電（睡眠功耗更低），長期部署可搭配太陽能 + 18650 電池
- 廣播群組通話時，SX1262 半雙工限制可能導致中繼節點偶爾漏包，Codec2 解碼端需做靜音填充處理
- 群組通話使用 DST_ID = 0xFFE0~0xFFEF（群組 ID）或 0xFFFF（全體廣播），點對點私聊使用指定 DST_ID
- 點對點訊息有 ACK 確認，廣播/群組訊息不回 ACK
- Device ID 出廠燒錄，OLED 螢幕顯示，APP 可編輯暱稱
- 新增聯絡人支援手動輸入 ID 和廣播探測自動發現兩種方式
- WiFi AP 的 SSID/密碼應可透過 APP（USB 或 WiFi）設定，避免寫死；預設 SSID 帶 Device ID
- WiFi UDP 傳輸不保證送達順序，APP 端應根據封包 SEQ 排序或丟棄亂序封包
- USB host 接上不代表有 APP（可能只供電）；需靠 F-053 握手區分資料端與純供電
- 兩傳輸層（USB+WiFi）可同時橋接，無「使用模式」切換；中繼能力恆開
- 韌體 OTA（F-060~064）走 USB/WiFi 推送，需雙 app 分割區的 partition table

## 實機 bring-up 注意事項（Unit C6L 踩雷紀錄，完整見 docs/SPEC.md §11）

**初始化順序（否則整機不動）：**
- **必先呼叫 `M5.begin()`** 才能用 `M5.Display/BtnA/Speaker`，否則開機即當機
- `DeviceConfig` 要值初始化 `cfg{}`，否則新機 NVS 空時讀到 0xA5 垃圾（SSID 曾變 `A5A5...`）

**LoRa / I2C（否則 LoRa 不通）：**
- SX1262 **RESET 在 I2C 擴充晶片 PI4IOE5V6408（0x43, SDA=10/SCL=8）的 P7**，須先初始化擴充晶片拉高 P7 釋放 reset，否則 `radio.begin()` 卡死整個 loop
- **`M5.begin()` 會佔用 Wire**，存取擴充晶片要先 `Wire.end()` 再 `Wire.begin(10,8)` 強制切腳位
- `radio.begin` 必須指定 **TCXO DIO3 = 3.0V**；RF 開關用 `setDio2AsRfSwitch(true)`
- SX1262 腳位：SCK20/MISO22/MOSI21/NSS23/DIO1=7/BUSY19/RESET=NC
- **Device ID 用 MAC NIC 後兩碼**（`esp_read_mac` 的 mac[4],[5]），**勿用 `getEfuseMac()&0xFFFF`**（OUI 前綴會全機撞號）

**實機觀測 / 測試：**
- 原生 USB(HWCDC) serial 重置後 host 會斷線、難穩定讀 log → **改用 WiFi `GET /version`（TCP）遙測**或 loop 心跳
- **Windows 防火牆擋回傳 UDP**，PC 端診斷請用 HTTP/TCP；手機連 WiFi 不受影響
- 多機測試：softAP 同網段 192.168.4.x，PC 雙網卡會路由衝突 → 用手機或裝置自身 OLED 觀測
- 保留畫面（SOS 警示）勿用無號數 `now-(millis()+N)`（會下溢），用 `(int32_t)(holdUntil-now)>0`
- 雙機 LoRa 收發 + SOS 已實機驗證通過（RSSI -27、Rx 累加）；兩台 OLED 皆正常（OLED 偶發開機不亮為暫態，重上電/重燒可恢復，非硬體缺陷）

### Android App 實機注意事項（手機→WiFi→C6L→LoRa 已端到端驗證）

- **Android 無對外網路的 WiFi 路由**：C6L AP 無網際網路，Android 會把 UDP 送往行動網路 → 連不到 192.168.4.1。需 `ConnectivityManager.BindProcessToNetwork` 綁定 WiFi（NetworkRequest `RemoveCapability(Internet)`）+ manifest `CHANGE_NETWORK_STATE`
- **Blazor 路由勿重複**：兩頁同為 `@page "/"`（如範本 Index 與自訂首頁）會卡 Loading（Router 排序例外）；文字頁設為首頁、PTT 改 /ptt
- **Codec2 native lib 未編譯**：PTT 的 `Codec2.Decode` 會 DllNotFound 崩潰，需 try/catch 並僅通話中解碼；首頁不要用到它
- **OTA http**：manifest 需 `usesCleartextTraffic="true"`（Android 9+ 擋明文）
- **adb 安裝 Debug APK**：需 `-p:EmbedAssembliesIntoApk=true`，否則 Fast Deployment 找不到 assemblies → SIGABRT
- **實機除錯**：用 `Console.WriteLine`（→ logcat `DOTNET` tag）；`adb logcat -s DOTNET` 過濾（無網路 WiFi 時 NetworkMonitor 會狂刷洗掉日誌）
- `ConnectAsync` 要冪等（已連線直接返回），避免重複建 UdpClient 弄死接收迴圈
