# LoRa PTT 語音對講系統

## 專案目標

使用兩組 Unit C6L（ESP32-C6 + SX1262）模組，搭配自製手機 APP，實現點對點的文字與語音通訊。語音採用 PTT（Push-to-Talk）半雙工模式，類似對講機體驗。

## 硬體規格

- **模組：** Unit C6L（M5Stack）
  - MCU: ESP32-C6（WiFi 6 + BLE 5）
  - LoRa: SX1262
  - 頻段: 920-925 MHz（台灣 ISM）
- **數量：** 2 組（一端一個）
- **天線：** 920MHz LoRa 天線 × 2（確認 SMA / IPEX 接頭）
- **手機：** Android / iOS，內建麥克風與喇叭，支援 BLE

## 系統架構

### 直連模式

```
手機A（錄音/播放） ←BLE→ C6L(A) ←LoRa 920MHz→ C6L(B) ←BLE→ 手機B（錄音/播放）
```

### 中繼模式（超出直連範圍時）

```
手機A ←BLE→ C6L(A) ←LoRa→ C6L(R) 中繼 ←LoRa→ C6L(B) ←BLE→ 手機B
                              │
                          不接手機，USB 供電
                          純轉發封包
```

### 群組通話模式（節點同時通訊+中繼）

```
手機A ←BLE→ C6L(A) ←──LoRa──→ C6L(B) ←──LoRa──→ C6L(C) ←BLE→ 手機C
                                  ↑
                                 BLE
                                  ↓
                                手機B（同時通話 + 中繼）
```

- C6L 作為 BLE ↔ LoRa 透傳橋接器（bridge）
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

### BLE 資料量評估

| 項目 | 數值 |
|------|------|
| Codec2 每秒資料量 | 300 bytes |
| 每包 payload（200ms） | ~60 bytes |
| 封包 overhead（header 8B + MAC 4B） | +12 bytes |
| 加密後每包總大小 | ~72 bytes |
| BLE 5.0 單包上限 | ~244 bytes ✅ |
| LoRa 單包上限 | ~255 bytes ✅ |

## 安全機制

### 威脅模型

LoRa 是開放頻段無線電，任何同頻段接收器都能收到封包，需防範：

| 威脅 | 說明 | 對策 |
|------|------|------|
| 竊聽 | 第三方用 SDR 或同款 LoRa 模組直接收聽 | AES-128-CTR 加密 payload |
| 偽冒 | 偽造封包假裝是合法裝置 | HMAC-SHA256 訊息驗證碼 |
| 重放攻擊 | 錄下封包重新播放 | 遞增序號（nonce），丟棄舊序號 |

### 加密方案：AES-128-CTR + HMAC-SHA256

- **AES-128-CTR：** 串流加密模式，適合語音連續資料，不需 padding，加解密邏輯相同
- **HMAC-SHA256（截斷 4 bytes）：** 驗證封包完整性與來源身份
- **Pre-Shared Key（PSK）：** 兩台 C6L 預先燒入相同金鑰（16 bytes AES key + 32 bytes HMAC key）
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
| DST_ID | 2 bytes | 目標接收者 ID（0xFFFF = 廣播） |
| HOP | 1 byte | 剩餘跳數，每次中繼轉發減 1，歸 0 丟棄（預設 MAX_HOP = 3） |
| SEQ | 2 bytes | 遞增封包序號，作為 AES-CTR nonce 一部分，防重放 |
| TYPE | 1 byte | 0x01=文字, 0x02=語音, 0x03=控制/心跳 |
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
   - 是自己或廣播 → AES-128-CTR 解密 payload → 交給 BLE
   - 不是自己且 HOP > 0 → 進入中繼轉發流程（見 Mesh Relay 章節）
6. 更新該 SRC_ID 的最後序號記錄

### 金鑰管理

- **初始版本：** PSK 寫死在韌體中（兩台燒同一組），適合個人使用
- **未來擴展：** 可透過 BLE 安全通道（加密配對後）從 APP 下發金鑰，支援換鑰

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
- BLE：維持低功耗連線（Connection Interval 拉長）
- 整機平均功耗極低

**通話模式（PTT 進行中）：**
- SX1262：連續 RX（即時收語音封包，不能漏幀）
- ESP32-C6：全速運行，處理加解密與 BLE 轉發
- BLE：Connection Interval 縮短，確保語音資料即時傳輸
- 通話結束後（放開 PTT + 3 秒無封包）→ 自動退回待機模式

### Preamble 策略

- 待機模式下，發送端第一個封包使用加長 preamble（覆蓋接收端的睡眠週期）
- 進入通話模式後，後續語音封包使用正常長度 preamble（接收端已切為連續 RX）
- 這樣只有第一個封包有額外延遲，後續語音串流不受影響

## Mesh Relay（中繼轉發）

### 概念

當兩端距離超出 LoRa 單跳傳輸範圍，可在中間放置一或多台 C6L 作為中繼節點，自動轉發封包：

```
手機A ←BLE→ C6L(A) ←LoRa→ C6L(R) 中繼 ←LoRa→ C6L(B) ←BLE→ 手機B
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
  → DST_ID 是自己？→ 交給 BLE 處理
  → DST_ID 不是自己（含廣播 0xFFFF）？
      → HOP > 0？→ HOP-- → 重新 LoRa 發送（轉發）
      → HOP == 0？→ 丟棄
```

### 一般節點的中繼能力

每個一般節點也可同時當中繼（可設定開關）：

```
收到 LoRa 封包
  → DST_ID 是自己或廣播？→ 處理（解密 + 交給 BLE）
  → 同時如果 HOP > 0 且 relay_enabled = true → 也轉發
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

### 節點模式設定

韌體透過編譯旗標或 BLE 指令切換模式：

| 設定項 | 一般節點 | 中繼專用節點 |
|--------|---------|------------|
| BLE Server | 開啟 | 關閉（省資源） |
| relay_enabled | 可選 | 強制開啟 |
| 供電方式 | 電池 / USB | USB 長期供電（或太陽能） |
| 手機連接 | 需要 | 不需要 |
| RxDutyCycle | 待機時啟用 | 可啟用（無通話時省電） |

### 部署建議

- 中繼節點放制高點（屋頂、山頂、高樓），搭配高增益全向天線（5dBi+）效果最佳
- 多個中繼節點可形成 mesh 網路，封包自動沿可達路徑轉發
- 城市環境建議每 1~3 km 一個中繼節點
- 空曠地形單跳可達 5~15 km，中繼需求較低

### 廣播群組通話（通訊+中繼同時運作）

一般節點可以同時作為通話端和中繼端。典型場景：A、B、C 三點通訊，A-C 距離過遠無法直連，B 同時通話並中繼。

```
手機A ←BLE→ C6L(A) ←──LoRa──→ C6L(B) ←──LoRa──→ C6L(C) ←BLE→ 手機C
                                  ↑
                                 BLE
                                  ↓
                                手機B（B 同時通話 + 中繼）
```

**使用廣播模式（DST_ID = 0xFFFF）** 實現群組通話，每個節點收到封包後同時做兩件事：

```
收到 LoRa 封包，DST_ID = 0xFFFF（廣播）
  → MAC 驗證通過 + SRC_ID 不是自己 + 去重檢查通過
  → ① 自己處理：解密 payload → BLE Notify → 手機播放
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

- **功能：** 純粹 BLE ↔ LoRa 雙向透傳
- **BLE：** 作為 GATT Server，提供 Write（手機→C6L）和 Notify（C6L→手機）Characteristic
- **LoRa：** 使用 SX1262 驅動，收到 BLE 資料就 LoRa 發送，收到 LoRa 資料就 BLE Notify
- **不使用 Meshtastic 韌體**（Meshtastic 不支援語音串流，自寫精簡韌體延遲更低）

### 韌體目錄結構

```
firmware/
├── platformio.ini
├── src/
│   ├── main.cpp             # 主程式：初始化 BLE + LoRa + Crypto
│   ├── ble_service.h/.cpp   # BLE GATT Server
│   ├── lora_handler.h/.cpp  # SX1262 LoRa 收發
│   ├── crypto.h/.cpp        # AES-128-CTR 加解密 + HMAC 驗證（mbedtls）
│   ├── packet.h/.cpp        # 封包格式定義、組包/解包（含 SRC_ID/DST_ID/HOP）
│   ├── relay.h/.cpp         # 中繼轉發邏輯、去重快取（ring buffer）
│   └── power_mgr.h/.cpp     # 電源狀態機：待機↔通話模式切換、RxDutyCycle 控制
└── lib/
```

## APP（手機端）

### 框架：.NET MAUI Blazor Hybrid

- 支援 Android + iOS
- BLE 通訊使用 Plugin.BLE（NuGet: Plugin.BLE）

### APP 模組

1. **BLE 通訊模組**
   - 掃描並連接 C6L
   - 訂閱 Notify Characteristic（接收 LoRa 資料）
   - 寫入 Write Characteristic（傳送資料給 LoRa）

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
   - 錄音 PCM → Codec2 encode → BLE 傳送
   - BLE 接收 → Codec2 decode → PCM 播放

### APP 目錄結構

```
app/
├── LoRaPTT.sln
├── LoRaPTT/
│   ├── MauiProgram.cs
│   ├── Services/
│   │   ├── BleService.cs         # BLE 掃描、連線、讀寫
│   │   ├── AudioRecordService.cs # 平台錄音抽象
│   │   ├── AudioPlayService.cs   # 平台播放抽象
│   │   └── Codec2Service.cs      # P/Invoke Codec2
│   ├── ViewModels/
│   │   └── MainViewModel.cs      # PTT 狀態、連線狀態
│   ├── Pages/
│   │   └── MainPage.razor        # PTT 按鈕 UI
│   └── Platforms/
│       ├── Android/
│       │   ├── AudioRecordImpl.cs
│       │   └── AudioPlayImpl.cs
│       └── iOS/
│           ├── AudioRecordImpl.cs
│           └── AudioPlayImpl.cs
└── libs/
    └── codec2/                   # Codec2 native libraries
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
- 確認 BLE 連線穩定

### Phase 2：BLE ↔ LoRa 透傳韌體
- 寫 Arduino 韌體，實現 BLE GATT Server
- 實現 LoRa 收發
- 用手機 BLE 除錯工具（nRF Connect）測試雙向透傳

### Phase 3：APP 文字版
- MAUI Blazor Hybrid 專案建置
- 整合 Plugin.BLE，連接 C6L
- 實現文字訊息收發

### Phase 4：PTT 語音
- 編譯 Codec2 native library
- 整合平台音訊錄製與播放
- 實現 PTT 按鈕邏輯
- 端到端語音通訊測試

### Phase 5：Mesh Relay 中繼
- 韌體加入中繼轉發邏輯與去重快取
- 新增節點模式設定（一般 / 中繼專用）
- 部署第三台 C6L 作為中繼節點測試
- 驗證多跳轉發的延遲與穩定性

## Coding Conventions

- **語言：** C#（.NET 8），韌體用 C++（Arduino）
- **命名：** 變數 lowerCamelCase、方法 UpperCamelCase、常數 ALL_CAPS、private fields 加 `_` 前綴
- **註解：** 全部使用繁體中文
- **非同步：** 所有 I/O 使用 async/await，不使用 .Result/.Wait()，CancellationToken 傳遞到最底層
- **錯誤處理：** 不允許空 catch、使用 ILogger、錯誤訊息繁體中文
- **BLE 相關：** 使用 Plugin.BLE NuGet 套件
- **UI：** Blazor Razor Pages + Bootstrap 5

## 注意事項

- 台灣 ISM 頻段 920-925 MHz 有 duty cycle 限制，語音串流需注意合規
- BLE 連線穩定性是關鍵，需處理斷線重連邏輯
- Codec2 的 P/Invoke 需要分別為 Android（arm64/x86_64）和 iOS 編譯
- LoRa 速率與距離互斥：SF 越低速率越高但距離越短，語音需要用 SF7 + BW500kHz 的高速設定
- 中繼節點需防止封包迴圈（去重快取 + HOP 遞減歸零丟棄）
- 中繼節點不解密 payload，只驗 MAC 後轉發，降低延遲
- 中繼節點放置高處搭配高增益天線（5dBi+）可大幅擴展覆蓋範圍
- 廣播群組通話時，SX1262 半雙工限制可能導致中繼節點偶爾漏包，Codec2 解碼端需做靜音填充處理
- 群組通話使用 DST_ID = 0xFFFF 廣播，點對點私聊使用指定 DST_ID
