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

```
手機A（錄音/播放） ←BLE→ C6L(A) ←LoRa 920MHz→ C6L(B) ←BLE→ 手機B（錄音/播放）
```

- C6L 作為 BLE ↔ LoRa 透傳橋接器（bridge）
- 所有音訊處理（錄音、編碼、解碼、播放）在手機端完成
- 不需要外接麥克風或喇叭模組

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
| BLE 5.0 單包上限 | ~244 bytes ✅ |
| LoRa 單包上限 | ~255 bytes ✅ |

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
│   ├── main.cpp          # 主程式：初始化 BLE + LoRa
│   ├── ble_service.h/.cpp   # BLE GATT Server
│   └── lora_handler.h/.cpp  # SX1262 LoRa 收發
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
