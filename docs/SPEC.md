# LoRa PTT 通訊系統 — 產品規格書

> 版本：1.3  
> 日期：2026-06-07  
> 狀態：Unit C6L 實機 bring-up 完成（WiFi/HTTP/UDP 橋接、F-053 握手、OLED、按鈕、**LoRa 雙機收發**、SOS 皆實機驗證）。新增 §2.4 實機腳位、§5.4 線路幀、§11 注意事項

---

## 1. 產品概述

基於 M5Stack Unit C6L（ESP32-C6 + SX1262）模組，搭配自製手機 APP，實現多節點的文字與語音通訊系統。語音採用 PTT（Push-to-Talk）半雙工模式，所有節點同時具備端點通訊與中繼轉發能力，封包以洪泛（Flooding）方式傳播，適用於戶外活動、災難救援等無基礎網路設施的場景。

---

## 2. 硬體規格

### 2.1 核心模組：M5Stack Unit C6L（型號 U202）

| 項目 | 規格 |
|------|------|
| MCU | ESP32-C6（RISC-V，WiFi 6 + BLE 5 + Zigbee 3.0） |
| LoRa 晶片 | SX1262（+22dBm TX，-147dBm RX） |
| LoRa 頻段 | 868~923 MHz（台灣 ISM：920-925 MHz） |
| 螢幕 | 0.66" OLED 單色（SSD1306，64x48） |
| 按鈕 | User Button x 1 |
| 蜂鳴器 | Buzzer x 1 |
| RGB LED | WS2812C x 1 |
| 介面 | USB Type-C（供電 + CDC Serial）、HY2.0-4P Grove |
| 天線接口 | RP-SMA x 2（WiFi 天線 + LoRa 天線） |
| 過壓保護 | AW32901FCR |
| 尺寸 | 62 x 24 x 8 mm |
| 安裝 | LEGO 相容孔位 |

### 2.2 附件（隨盒）

- RP-SMA 2.4GHz WiFi 天線 x 1（3dBi）
- RP-SMA 868MHz LoRa 天線 x 1（3dBi）
- HY2.0-4P Grove 連接線 x 1（20cm）

### 2.3 供電方式

供電來源與「節點是否有 APP 連線」彼此獨立，互不決定行為。

| 供電來源 | 介面 | 常見搭配 |
|---------|------|---------|
| 手機 USB-C 供電 | USB Type-C | 手機隨身帶（手機同時可能是 APP 端，也可能只供電） |
| 行動電源 | USB Type-C | 定點/基站、無人中繼 |
| 太陽能 + 鋰電池 | Grove 5V（睡眠功耗更低） | 長期無人中繼站 |

### 2.4 實機腳位（依 m5stack/meshtastic-firmware variant，已實機驗證）

**SX1262 LoRa（SPI）：**

| 訊號 | GPIO | 備註 |
|------|------|------|
| SCK / MISO / MOSI / NSS | 20 / 22 / 21 / 23 | SPI |
| DIO1（IRQ） | 7 | |
| BUSY | 19 | |
| RESET | **無 GPIO**（RADIOLIB_NC） | 改由 I2C 擴充晶片控制（見下） |
| RF 開關 | SX1262 **DIO2 內建**（`setDio2AsRfSwitch`） | 不用外部 GPIO |
| TCXO | **DIO3 供電 3.0V** | `radio.begin` 需指定，否則晶振不起、初始化失敗 |
| 參數 | 920 MHz / BW500 / SF7 / CR4:5 / SyncWord 0x12 / +22dBm | |

**PI4IOE5V6408 I2C IO 擴充晶片（I2C：SDA=10, SCL=8，位址 0x43）：**

| 腳 | 功能 | 備註 |
|----|------|------|
| P7 | SX1262 NRST（reset） | 開機需拉高釋放，否則 `radio.begin` 卡死 |
| P6 | RF 天線開關 | 初始化拉高 |
| P5 | LNA Enable | |
| P0/P1 | 使用者按鈕 | |

**其他：** OLED SSD1306（SPI，CS=6/DC=18/RST=15）、WS2812 RGB（GPIO2）、Buzzer（GPIO11）、GPS（GPIO4/5）。

> OLED/按鈕/蜂鳴器經 `M5.begin()`（M5Unified）即可使用；RGB/LoRa/擴充晶片需自行初始化。

---

## 3. 系統架構

### 3.1 整體拓撲

```
手機A ←USB-C/WiFi→ C6L(A) ←LoRa→ C6L(B) ←LoRa→ C6L(C) ←USB-C/WiFi→ 手機C
                                    ↑
                              USB-C 和/或 WiFi
                                    ↓
                                  手機B
```

- 每個 C6L 同時是**端點**（收發自己的訊息）和**中繼**（轉發他人的封包），**中繼能力恆開**
- **沒有「使用模式」需要切換**：節點行為僅由「目前有哪些傳輸層連著 APP」決定
- 無任何 APP 連線的 C6L 自動成為**純中繼站**（與供電來源無關）

### 3.2 手機 ↔ C6L 通訊（雙傳輸層，可同時運作）

C6L 提供兩種傳輸層，**可同時啟用**，上層封包格式完全一致；LoRa 收到的封包會推送給**所有已連線（已握手）的傳輸層**，任一傳輸層送來的資料都會發 LoRa。

| 傳輸層 | 連線方式 | 啟用條件 | 特性 |
|--------|---------|---------|------|
| USB Serial（CDC） | Type-C 接手機 | 該端 APP 完成握手（見 F-053） | 延遲最低、不佔無線頻寬 |
| WiFi AP + UDP | 手機連 C6L 熱點，UDP:5000 | WiFi AP 預設常開（可關省電） | 手機可離開數十公尺 |

**重點：USB host 接上只代表「可能供電」，不代表有 APP。** 必須等該端 APP 送出握手封包（F-053），C6L 才把該傳輸層視為資料通道。

> **典型場景（電量共享）：** A 手機有 APP 但電量低、B 手機有電但沒 APP。
> → C6L 用 Type-C 接 B 手機（純供電）、同時開 WiFi AP 讓 A 手機連入傳訊。
> 兩傳輸層同時運作，缺一不可。

#### 3.2.1 傳輸層實作狀態（⚠ 重要：USB 尚未實作）

| 傳輸層 | 韌體端 | APP 端 | 狀態 |
|--------|--------|--------|------|
| **WiFi AP + UDP** | ✅ `wifi_service` | ✅ `WiFiCommService`（含心跳離線偵測） | **已實機驗證**（手機/PC 皆通） |
| **USB Serial（CDC）** | ✅ `usb_serial_service`（2-byte 長度前綴幀） | ✅ `Platforms/Android/UsbSerialImpl`（CDC-ACM）+ `CommRouter` 自動選 USB/WiFi | **🟠 實機半通：手機→C6L OK、C6L→手機不通（HWCDC/DTR）** |

> **F-054 實機發現（2026-06-09，fw 0.5.1）**：USB 偵測+權限+**手機→C6L 方向都通**（C6L 收到 hello、OLED 顯示 `APP:USB`）；但 **C6L→手機 的 ack 回不來** → 手機卡「握手中」。
> 根因：韌體 `usb_serial_service.send()` 的 `setTxTimeoutMs(0)` 在 HWCDC 判定「無 host（DTR 未拉起）」時把 TX 丟棄；**Android USB OTG host 未可靠拉起 DTR**（`UsbSerialImpl` 的 `SET_CONTROL_LINE_STATE` 疑未生效）。移除 send() 的 `isConnected()` 守衛**無效**（commit `bdc7d1b`），證明問題在 HWCDC TX 的 DTR 判定，**正是本表上方預警的「HWCDC over OTG 不穩」**。
> **下一步（需專門 debug bench）**：① `UsbSerialImpl` 加 log（SET_CONTROL_LINE_STATE 回傳值、ReadLoop 讀到幾 byte、SendAsync 結果）；② 試「USB 有 APP 時改用非 0 tx timeout」韌體條件化；③ 穩定 adb（C6L softAP 或有線）+ C6L serial console 對拍。**WiFi 仍為唯一可靠傳輸層。**

> **F-054 已實作（commit `3334135`）**：Android USB OTG CDC-ACM（列舉 VID `0x303A`、權限、開埠、DTR、收發、2-byte 長度幀、收訊重同步）。`CommRouter` 連線時先試 USB（偵測到 C6L 才成功）、失敗落回 WiFi。**尚未實機驗證**。
>
> **回家怎麼測 F-054**：手機 USB-C 接 C6L → 開 App → 連接 → CommRouter 會先試 USB → 跳「允許 USB 存取」系統對話框 → 允許 → 看是否顯示裝置 ID（= USB 連上）。⚠ 若連不上或不穩，極可能是 **HWCDC over OTG 不穩**（見下方風險），那就確認此路不可靠、退回 WiFi 為主。除錯用 `adb logcat -s DOTNET:*` 看 `UsbSerialImpl` 的 log。

**F-054 USB Serial 傳輸層（待實作）實作規劃：**
1. 加 .NET Android USB serial 套件（`UsbSerialForAndroid`），csproj net7-android。
2. `AndroidManifest`：`android.hardware.usb.host` feature + `USB_DEVICE_ATTACHED` intent filter + 執行期 USB 權限請求。
3. `Platforms/Android/UsbSerialImpl.cs` + 補完 `UsbSerialCommService`：列舉 USB → 認 **ESP32-C6 CDC（VID `0x303A`）** → 請權限 → 開埠 115200 → 收發**含 2-byte 大端長度前綴**（對應韌體 `usb_serial_service`），並做與 PC 客戶端相同的**重同步解析**（容忍 debug log，雖韌體已在 USB 有 APP 時抑制 log）。
4. `MauiProgram` 改為可同時註冊兩傳輸層、或連線畫面（F-030 連線畫面）讓使用者選 USB/WiFi。
5. 與 WiFi 共用 `ICommService`，上層（訊息/語音）零改動。

> **⚠ 風險（先記）：** C6L 原生 **HWCDC USB 對「會開埠的 host」實測不穩**（PC 端資料時有時無、重置後難讀，見交接文件）。手機 OTG 很可能踩同樣問題。**實作前建議先用手機 USB 終端機 App 驗證 HWCDC 在 OTG 下到底穩不穩，通且穩才值得投入完整實作。** WiFi 為目前唯一已驗證穩定的傳輸層。

### 3.3 C6L ↔ C6L 通訊

- LoRa 920-925 MHz（台灣 ISM 頻段）
- 洪泛式傳播：每個節點收到非自己的封包就轉發
- TTL 限制傳播範圍，去重機制防止迴圈

---

## 4. 功能規格

### 4.1 裝置管理

| 功能 | 說明 |
|------|------|
| **F-001** Device ID | 2 bytes（0x0001~0xFFFE）；**預設由晶片 MAC 的 NIC 後兩碼衍生**（每顆自動唯一），可經 APP 設定覆寫、存 NVS；**OLED 開機頁顯示真實 ID**。⚠️ 勿用 `getEfuseMac()&0xFFFF`（那是所有 Espressif 共用的 OUI 前綴，會撞號） |
| **F-002** 暱稱 | APP 上可編輯裝置暱稱，存於裝置 NVS，顯示於聊天介面 |
| **F-003** 手動新增聯絡人 | 看對方 C6L 螢幕上的 ID，在 APP 手動輸入 |
| **F-004** 廣播探測發現 | APP 發送廣播 PING，附近裝置回覆 ID + 暱稱，列出清單選擇加入通訊錄 |

### 4.2 文字訊息（Phase 1 核心功能）

| 功能 | 說明 |
|------|------|
| **F-010** 點對點文字 | 選擇一個聯絡人發送文字訊息，有 ACK 確認 |
| **F-011** 廣播文字 | 發給所有人，無 ACK |
| **F-012** 群組文字 | 發給特定群組成員，無 ACK |
| **F-013** 文字長度限制 | 單包最大 243 bytes（約 80 個中文字）；APP 端**事前驗證並提示字數，超過禁止送出**（不丟例外） |
| **F-014** 聊天記錄 | 對話泡泡 UI，區分收發方 |
| **F-015** 傳送狀態 | 點對點：顯示「傳送中...」直到收到 ACK；廣播/群組：發出即完成 |

### 4.3 群組功能

| 功能 | 說明 |
|------|------|
| **F-020** 建立群組 | APP 上建立群組，分配群組 ID（0xE0~0xEF，最多 16 個群組） |
| **F-021** 加入/離開群組 | 在 APP 上管理自己所屬的群組 |
| **F-022** 群組訊息 | 發送到群組 ID，**僅屬於該群組的成員**顯示在群組聊天室 |

### 4.4 LoRa 網路 — 洪泛中繼

| 功能 | 說明 |
|------|------|
| **F-030** 自動中繼 | 每台 C6L 收到非自己的封包且 TTL > 0，自動轉發（TTL-1） |
| **F-031** TTL 限制 | 封包帶 HOP 欄位，依類型設定初始值（文字=5、SOS=15、語音=3），歸零停止轉發 |
| **F-032** 去重機制 | (SRC_ID + SEQ) 環形快取（64~128 筆），防止同封包重複轉發 |
| **F-033** 無人中繼站 | C6L 無任何 APP 連線（握手）時即為純中繼，與供電來源無關 |
| **F-034** RSSI 顯示 | 韌體將收訊 RSSI 隨封包一併傳給 APP；APP 聊天訊息與 OLED 皆顯示最近 RSSI |
| **F-036** NodeDB 節點資料庫 | 參考 Meshtastic：從收到的**任何封包自動建檔**（ID/RSSI/最後聽到，最多 16 筆 LRU），PING 回覆學暱稱、POS/SOS 學座標。OLED 新增「Nodes」頁（總數 + 最近聽到前 4 台 ID/秒數）。✅ FW 0.7.5；**APP 端 ✅ 「節點」頁**（`NodeRegistry` 從收到的封包累積，顯示 ID/暱稱/RSSI/最後聽到/座標 + 探測鈕）；地圖視覺見 F-037 |
| **F-037** 節點地圖（規劃中）🟡 | APP 用 **MapLibre GL JS**（嵌 Blazor WebView，**一套跨 Android/iOS**，避開 Meshtastic 原生 MapLibre/MapKit 雙套分裂）顯示自己 + 各節點 markers（座標來自 F-074），點選顯示 ID/暱稱/RSSI/座標/最後聽到。底圖可切換 **線上 / 離線圖磚 / 雷達**。**階段一 ✅（待實機驗收）**：MapLibre GL JS（本地打包）+ **OpenFreeMap** 線上向量圖磚（免費免 key 可商用）；地圖頁「地圖」分頁、自己(青)/節點(黃) markers、點 marker→下方浮卡、節點清單 dialog、回到自己。**底圖切換**：街道(OpenFreeMap) ↔ **衛星(NLSC 正射影像 PHOTO2 WMTS，官方免費)**。**氣象雷達(CWA)疊加 ✅**：O-A0058-005 透明整合回波圖（範圍經 115.00-126.50/緯 17.75-29.25，由 CWA 開放資料 API 驗證）；oi-rain 鈕開關、半透明 0.65。S3 圖檔無 CORS → 由 .NET 原生 HTTP 抓回轉 data URL 餵 MapLibre image source。**階段二 v1 ✅（待實機驗收）**：衛星(NLSC)離線——`loraoff://` 本機協定（MapLibre v4 addProtocol，離線優先 + 線上自動快取）、「下載此區」鈕（目前畫面 ~z16，底部半透明進度條 + 取消）、圖磚存 AppData 檔案庫（sat/z/x/y.jpg），多次下載自然累積覆蓋、重下載＝覆蓋更新。**v2 待做**：街道離線、已下載區域管理（列表/刪除/容量）、線上↔離線手動切換、MBTiles 化、自算 OSM 向量打包。⚠ NLSC 散布前需再確認重製授權 |
| **F-035** 管理式洪泛（規劃中）🟡 | 參考 Meshtastic：轉發前加退避窗 + 「聽到別人已轉發就抑制自己」，降低多節點碰撞與廣播風暴。**兩半對語音影響相反**：退避延遲傷語音即時性、抑制反而減半雙工碰撞掉幀。**設計主軸「兩邊都顧到」(因轉發時機與 SF/BW 無關，可依封包類型分開，無「不同 SF 聽不到」問題)**：① **文字/SOS/定位** → 完整管理式洪泛(退避窗較大、SNR 越低退避越短讓邊緣節點先轉、其餘聽到即抑制)；② **語音** → 退避窗極小或跳過(保即時)、但**保留抑制**(照樣減碰撞)。退避建議用硬體亂數 `esp_random()` 錯開。**優先度低**：節點密(廣播風暴)或群組語音碰撞嚴重時才需要；現 3~4 台無腦洪泛+去重(F-032)夠用 |

### 4.5 ACK 確認機制

| 傳送方式 | ACK 行為 | APP 顯示 |
|----------|---------|----------|
| 點對點（DST_ID = 特定 ID） | 目標收到後回 ACK | 收到 ACK 前顯示「傳送中...」，收到後正常顯示，timeout 不報錯 |
| 廣播（DST_ID = 0xFFFF） | 不回 ACK | 發出即完成 |
| 群組（DST_ID = 0xFFE0~0xFFEF） | 不回 ACK | 發出即完成 |

> ACK 封包走洪泛回傳（因為不知道回去的路徑），TTL 同 MAX_HOP = 3。SEQ 由 APP 擁有，作為 ACK 關聯依據。

### 4.6 C6L 本機互動

| 功能 | 說明 |
|------|------|
| **F-040** OLED 分頁顯示 | **5 頁**，短按切換：頁1 ID/暱稱/APP/**FW 版本/WiFi:ON/OFF**；頁2 WiFi（ON 時 SSID/IP，OFF 時顯示 `WiFi OFF`）；頁3 LoRa（頻率/SF/最近 RSSI）；頁4 中繼統計（轉發數/收發數）；**頁5 GPS（FIX/衛星數/經緯度）** |
| **F-041** User Button | 短按：切換顯示頁面（**螢幕睡著時第一下只喚醒不換頁**）；**長按 3 秒：開/關 WiFi AP（省電）**；長按 6 秒：重置設定為出廠值 |
| **F-042** RGB LED 狀態燈 | 綠色常亮=USB 已連；藍色常亮=WiFi AP 啟動；藍色閃爍=等待連入；紅/黃閃爍=LoRa 收發中；紅色常亮=錯誤 |
| **F-043** Buzzer 提示 | 收到屬於自己/廣播/群組的訊息時短響一聲（無人中繼站亦可選擇對經過封包嗶聲） |
| **F-044** 螢幕閒置自動關 | 閒置 **20 秒**關 OLED（省電 + 防烙印）；**按鍵或收到訊息喚醒**；SOS 畫面強制亮起（Meshtastic 式）。✅ FW 0.7.2 |
| **F-045** WiFi 閒置自動關 | WiFi AP 開啟後 **5 分鐘**無 station 連著且無 APP 握手 → 自動關 AP 省電（~40mA）；**有人連著就絕不關**（避免反覆斷線重連）；長按手動開關不受限。✅ FW 0.7.2 |

### 4.7 連線與設定

| 功能 | 說明 |
|------|------|
| **F-050** 預設 AP 設定 | SSID: `LoRaPTT_{DEVICE_ID}`，密碼: 預設值，UDP Port: 5000 |
| **F-051** APP 修改設定 | APP 送 JSON 指令修改 SSID/密碼/暱稱/LoRa 頻率/**定位廣播間隔(`pos_interval`)**；**設定指令與資料封包分流**（避免被當 LoRa 封包送出），存 NVS 後回 `{"status":"ok"}`。設定頁 ✅（含定位間隔欄位）；端到端待實機驗收 |
| **F-052** 設定持久化 | 儲存到 ESP32-C6 NVS，斷電不遺失 |
| **F-053** 連線握手 | APP（USB 或 WiFi）連上後先送 hello（含暱稱/APP 版本）；C6L 收到才將該傳輸層標記為「有 APP」並開始雙向橋接。純供電（無 hello）不送資料、視為中繼。回應含 C6L 韌體版本（供 F-064） |

### 4.8 韌體更新（OTA）

| 功能 | 說明 |
|------|------|
| **F-060** OTA 推送 | APP 把韌體 `.bin` 推送到 C6L；走 **WiFi HTTP（TCP，port 80，`POST /update`）** 確保可靠（UDP 不適合韌體影像）；不走 LoRa（影像過大） |
| **F-061** 雙分割區更新 | 以 `Update.h` 寫入非作用中的 OTA 分割區（app0/app1），校驗通過後切換開機分割區並重開機 |
| **F-062** 進度顯示 | OLED 顯示更新進度 %（上傳期間直接繪製進度條）；寫入/校驗失敗則中止並保留舊韌體 |
| **F-063** 失敗回滾 | 新韌體開機未通過自檢 → 回退舊版。已啟用 `CONFIG_BOOTLOADER_APP_ROLLBACK_ENABLE`（sdkconfig.defaults），韌體 setup() 呼叫 `esp_ota_mark_app_valid_cancel_rollback()` 標記有效 |
| **F-064** 版本查詢 | APP 可讀取 C6L 韌體版本（握手 hello-ack 的 `fw_ver`，或 `GET /version`） |

> **手機 APP 本身的更新**走平台正常管道（Android APK/Play、iOS TestFlight/App Store），不由 C6L 負責。
>
> **韌體配置：** partition table 用 `default_16MB.csv`（含 app0/app1 雙 OTA 分割區，各 6.5MB），flash size 設 16MB 對應 Unit C6L 實機。OTA 走獨立 HTTP（TCP）通道，與 UDP:5000 資料通道分離。

### 4.9 SOS 緊急求救

| 功能 | 說明 |
|------|------|
| **F-070** SOS 封包 | TYPE=0x06，DST=0xFFFF（廣播），**HOP=15**（盡可能傳遠），Payload 含 Device ID + GPS 座標（若有）+ 時間戳 |
| **F-071** C6L 觸發 | User Button **快速連按 3 下（1 秒內）** → 發送 SOS。Buzzer 長嗶確認，OLED 顯示 "SOS SENT"。**不需手機也能求救** |
| **F-072** APP 觸發 | APP 主畫面 SOS 按鈕，**長按 3 秒觸發**（防誤觸）。附帶手機 GPS 座標 + 可選簡短文字 |
| **F-073** 收到 SOS | 所有節點：Buzzer 連續長響 3 秒 + RGB LED 紅色快閃 + OLED 顯示 "!!! SOS !!! ID:xxxx"；APP：全螢幕警示彈窗 + GPS 座標顯示 + 手機震動 |
| **F-074** 定位定時廣播 | 有 GPS fix 時定時廣播自身座標：TYPE=**0x07**，DST=0xFFFF，**HOP=1**（限制洪泛流量），Payload `[ID 2B][Lat 8B][Lon 8B]`（小端 double，格式同 SOS GPS）。**智慧定位**（FW 0.7.4，參考 Meshtastic）：移動 > 30m 即提早廣播(最短間隔 15 秒防狂發)、靜止則退回最大間隔 `pos_interval`(存 NVS，預設 **30 秒**，0=關，可經 set_config 遠端設定)。**韌體側 ✅；APP 端 ✅ 解析 0x07 → 餵入 NodeDB（節點頁顯示座標）；獨立地圖視覺待做** |

> SOS 封包重複發送 3 次（間隔 2 秒），提高到達率。不等 ACK。

---

## 5. 封包協議

### 5.1 LoRa 封包格式

```
┌──────────── 明文區（路由用） ─────────────┐┌── 加密區 ──┐┌─ 驗證 ─┐
│ SRC_ID (2B) │ DST_ID (2B) │ HOP (1B) │ SEQ (2B) │ TYPE (1B) │ PAYLOAD (NB) │ MAC (4B) │
└──────────────────────────────────────────┘└────────────┘└────────┘
```

| 欄位 | 大小 | 說明 |
|------|------|------|
| SRC_ID | 2 bytes | 原始發送者 Device ID |
| DST_ID | 2 bytes | 目標 ID：0x0001~0xFFFE=點對點；0xFFFF=廣播；0xFFE0~0xFFEF=群組 |
| HOP | 1 byte | 剩餘跳數，每次中繼 -1，歸 0 丟棄 |
| SEQ | 2 bytes | 遞增序號，防重放 + 去重依據 |
| TYPE | 1 byte | 0x01=文字；0x02=語音；0x03=控制/心跳；0x04=ACK；0x05=PING/探測；**0x06=SOS**；**0x07=定位（GPS 座標廣播）** |

**HOP 預設值（依封包類型）：**

| 封包類型 | MAX_HOP | 理由 |
|----------|---------|------|
| 一般文字 / 群組 / 廣播 | **5** | 日常使用，都市覆蓋 ~6-18 km |
| ACK | **5** | 與文字同級 |
| PING/探測 | **5** | 聯絡人發現 |
| **SOS 緊急** | **15** | 盡可能傳遠，人命最優先 |
| 語音（Phase 4） | **3** | 連續封包量大，避免壅塞 |
| PAYLOAD | 0~N bytes | 加密後的資料 |
| MAC | 4 bytes | HMAC-SHA256 截斷，驗證完整性 |

**標頭大小：** 8 bytes（明文區）+ 4 bytes（MAC）= 12 bytes overhead  
**可用 Payload：** 255 - 12 = 243 bytes

### 5.2 DST_ID 定址規則

| 範圍 | 用途 |
|------|------|
| 0x0001 ~ 0xFFFE | 點對點，指定單一裝置 |
| 0xFFFF | 全體廣播 |
| 0xFFE0 ~ 0xFFEF | 群組 ID（最多 16 個群組） |
| 0x0000 | 保留，不使用 |

### 5.3 節點收到封包的處理流程

```
收到 LoRa 封包
  │
  ├── 驗證 MAC → 失敗 → 丟棄
  │
  ├── 去重：(SRC_ID + SEQ) 在快取中？ → 是 → 丟棄
  │                                    → 否 → 加入快取
  │
  ├── DST_ID 判斷：
  │     ├── 是自己 → 解密 Payload → 推送給手機 APP
  │     │             └── TYPE == 需要 ACK？ → 回送 ACK 封包
  │     ├── 廣播 0xFFFF → 解密 Payload → 推送給手機 APP（自己也收）
  │     ├── 群組 0xFFE0~0xFFEF → APP 檢查是否屬於該群組 → 是則處理
  │     └── 不是自己 → 不推送給 APP
  │
  └── 中繼轉發判斷（同時進行）：
        ├── HOP > 0 且 SRC_ID 不是自己 → HOP-- → LoRa 重發
        └── HOP == 0 → 不轉發
```

> 廣播和群組封包：既推送給 APP 也做中繼轉發。

### 5.4 手機 ↔ C6L 線路幀格式（已實作）

手機與 C6L 之間（USB Serial 或 WiFi UDP）以「線路幀」封裝，第 1 byte 為類型：

```
LINK_DATA 0x01  資料（LoRa 封包）
    phone → C6L：[01][LoRa 封包]
    C6L → phone：[01][RSSI int16 BE][LoRa 封包]
LINK_CTRL 0x02  控制/設定（JSON, UTF-8），雙向
```

- **傳輸層邊界**：USB Serial 另加 2-byte 大端長度前綴；WiFi 一個 UDP datagram 即一幀。
- **CTRL JSON**：
  - 握手（F-053）：APP→`{"cmd":"hello","name":...,"app_ver":...}`；C6L→`{"status":"hello","device_id":N,"name":...,"fw_ver":...}`
  - 設定（F-051）：APP→`{"cmd":"set_config",...}`；C6L→`{"status":"ok"}`
- **DATA 方向差異**：C6L→phone 多帶 2-byte RSSI（int16 大端），phone→C6L 無。
- C6L 收到 DATA 後，**保留 APP 提供的 DST/SEQ/TYPE/PAYLOAD**，覆寫 SRC/HOP 並重算 MAC 才發 LoRa。

> **OTA 不走此線路幀**：韌體更新走獨立 HTTP（WiFi TCP，`POST /update`），見 §4.8。
> 另有 `GET /version` 回報韌體版本與（除錯用）LoRa 狀態。

---

## 6. APP 功能規格

### 6.1 框架

- .NET MAUI Blazor Hybrid
- 支援 Android + iOS
- UI：Blazor Razor Pages + Bootstrap 5

### 6.2 主要畫面

| 畫面 | 功能 |
|------|------|
| 連線畫面 | 偵測 USB 裝置 / WiFi SSID，選擇傳輸層連線；連上後送出握手（F-053） |
| 通訊錄 | 聯絡人列表，新增（手動輸入 ID / 廣播探測）、編輯暱稱 |
| 群組管理 | 建立群組、加入/離開群組 |
| 聊天室 | 對話泡泡、文字輸入、傳送狀態、RSSI 顯示 |
| PTT 語音 | PTT 按鈕（Phase 4） |
| 設定 | 裝置暱稱、WiFi AP 設定、LoRa 參數、韌體更新 OTA（後期） |

### 6.3 通訊模組

APP 與 C6L 的連線同樣可走 USB Serial 或 WiFi，兩者實作同一 `ICommService` 介面，上層（訊息/語音）不需知道底層傳輸。

| 模組 | 說明 |
|------|------|
| `ICommService` | 通訊抽象介面（USB Serial / WiFi 共用） |
| `UsbSerialCommService` | USB Serial CDC 實作 |
| `WiFiCommService` | WiFi UDP 實作 |

---

## 7. 安全機制

### 7.1 Phase 1（目前）— 不加密

- 災難場景優先確保通訊可靠性
- 降低系統複雜度與除錯難度
- 中繼節點可直接轉發，不需金鑰

### 7.2 Phase 2（後期）— 可選加密

- AES-128-CTR 加密 Payload
- HMAC-SHA256 截斷 4 bytes 驗證完整性
- Pre-Shared Key（PSK）機制
- 加密為可選功能，可在 APP 設定中開關

> Phase 1 的 MAC 欄位使用 CRC32 做完整性檢查（計算時跳過 HOP 欄位，使中繼遞減 HOP 不破壞 MAC、無需持有金鑰）。已於韌體與 APP 實作並驗證一致。

---

## 8. 電源管理

### 8.1 SX1262 RxDutyCycle 省電

- 待機：SX1262 每秒醒 5ms 偵測 preamble，平均功耗 ~0.05mA
- 通話：切換為連續 RX，確保語音封包不漏
- 通話結束 3 秒無封包 → 自動回待機

### 8.2 狀態機

```
待機模式（省電）──收到封包或按 PTT──→ 通話模式（全速）──放開 + 超時 3 秒──→ 待機模式
```

---

## 9. 開發階段

### Phase 1：驗證硬體

- 刷 Meshtastic 韌體到兩組 C6L
- 用 Meshtastic 官方 App 確認 LoRa 通訊正常
- 確認 USB Serial / WiFi 連線穩定

### Phase 2：透傳韌體

- Arduino 韌體：USB Serial（CDC）+ WiFi AP + LoRa 收發
- **雙傳輸層多工**：USB 與 WiFi 可同時橋接 LoRa（F-053 握手後啟用）
- OLED 顯示 + 按鈕切頁/WiFi 開關 + RGB LED 狀態
- 用測試工具驗證雙向透傳

### Phase 3：APP 文字版（核心功能）

- MAUI Blazor Hybrid 專案
- 連線管理（USB Serial / WiFi）
- 通訊錄（手動輸入 + 廣播探測）
- 群組管理
- 點對點/廣播/群組文字訊息
- ACK 確認機制
- 聊天記錄 UI

### Phase 4：PTT 語音

- 編譯 Codec2 native library（Android .so / iOS .a）
- 平台音訊錄製與播放
- PTT 按鈕邏輯
- 語音端到端測試

#### Phase 4 子模組實作狀態（⚠ 有兩個空殼/前置阻擋，勿當「語音已完成」）

| 子模組 | 狀態 | 說明 |
|--------|------|------|
| **Codec2 native lib** | ❌ **未編譯** | `Codec2Service` 的 P/Invoke 指向 `libcodec2`，但 `.so/.a` 從未編。**現在一進通話呼叫 encode/decode 會 `DllNotFoundException` 崩潰**。前置阻擋：必須先用 NDK/CMake 把 Codec2 編成 Android `arm64-v8a/.so`（iOS `.a`）。這是 Phase 4 唯一有「環境步驟」的部分，謹慎做。 |
| **Android 音訊（錄/播）** | ✅ 已實作 | `Platforms/Android/AudioRecordImpl`（`AudioRecord`）/`AudioPlayImpl`（`AudioTrack`）。 |
| **iOS 音訊（錄/播）** | ❌ **空殼** | `Platforms/iOS/AudioRecordImpl`/`AudioPlayImpl` 直接 `throw NotImplementedException`。⚠ **即使 Android 語音做完，iOS 仍完全無語音、一進通話即崩**——別把「Android 語音 OK」當成「語音功能完成」。iOS 要做需 `AVAudioEngine`（PlayAndRecord, 8000Hz Int16）。 |
| PTT 按鈕邏輯 / 累積送包 | 🟡 部分 | `MainViewModel` 有 PTT 框架，但依賴上面兩項才能端到端跑。 |

> **F-055 Codec2 native lib（前置阻擋）**、**F-056 iOS 音訊（待實作）** — 兩者皆「規劃有、實際空殼」，列此確保不被默默漏掉。

### Phase 5：中繼網路完善

- 洪泛中繼轉發 + 去重
- 多跳延遲與穩定性測試
- 無人中繼站部署測試

### 後期功能

| 功能 | 說明 | 優先級 |
|------|------|--------|
| AES-128 加密 | 可選加密，PSK 機制（Phase 2 安全機制） | 中 |
| 韌體 OTA 更新（F-060~064） | APP 透過 USB/WiFi 推送韌體，雙分割區 + OLED 進度 + 失敗回滾 | 中 |
| Grove 感測器擴展 | 溫濕度/GPS/PIR 等感測器資料回傳 | 低 |
| 長訊息分包 | 超過單包上限的文字自動分包/組包 | 低 |
| 離線訊息暫存 | 中繼節點暫存目標離線時的訊息，上線後投遞 | 低 |

---

## 10. 技術限制與注意事項

1. **台灣 ISM 頻段** 920-925 MHz 有 duty cycle 限制，語音串流需注意合規
2. **LoRa 半雙工** — SX1262 同一瞬間只能 RX 或 TX，中繼轉發時可能漏包，語音需做靜音填充
3. **LoRa 速率與距離互斥** — SF 越低速率越高但距離越短，語音建議 SF7 + BW500kHz
4. **OLED 64x48** — 顯示空間有限，需分頁顯示，字體選擇需考量可讀性
5. **iOS USB Serial** — 支援較受限，iOS 裝置可能需以 WiFi 連線為主
6. **洪泛頻寬** — 每個節點都轉發，節點密集時可能造成頻道壅塞，TTL 需合理設定
7. **USB 供電 vs APP** — USB host 接上不等於有 APP；需靠 F-053 握手區分，否則純供電手機會被誤判為資料端
8. **WiFi AP 功耗** — WiFi AP 常開會增加耗電，電池供電的無人中繼站可由 F-041 長按關閉 WiFi 省電

---

## 11. 實機 bring-up 注意事項與已知問題（踩雷紀錄）

> 以下為 Unit C6L 實機調試取得的關鍵經驗，務必遵守以免重蹈覆轍。

### 11.1 韌體初始化順序（會導致整機不動）

1. **必須先呼叫 `M5.begin()`** 才能使用 `M5.Display / M5.BtnA / M5.Speaker`；否則開機即當機（OLED 全黑、無輸出）。
2. **`DeviceConfig` 要值初始化（`cfg{}`）**：新機 NVS 為空時，`Preferences.getString` 不會寫入緩衝區，未初始化的堆疊填充值（0xA5）會被當成設定（曾導致 WiFi SSID 變成一串 `A5A5...`）。

### 11.2 LoRa / I2C（會導致 LoRa 不通）

3. **SX1262 沒有 GPIO RESET**，reset 線接在 I2C 擴充晶片 P7。**必須先初始化擴充晶片並拉高 P7 釋放 reset，才能 `radio.begin()`**，否則卡在等 BUSY（整個主迴圈停擺）。
4. **`M5.begin()` 會先佔用 Wire（I2C）**；要存取擴充晶片（GPIO10/8）必須 **`Wire.end()` 後再 `Wire.begin(10,8)`** 強制切換腳位，否則 re-begin 被忽略、掃不到 0x43。
5. **TCXO 必須在 `radio.begin` 指定 DIO3 供電 3.0V**，否則晶振不起、初始化失敗。
6. **RF 開關用 `setDio2AsRfSwitch(true)`**（SX1262 內建），不要自己用 GPIO 控制（GPIO4/5 其實是 GPS）。
7. **Device ID 用 MAC 的 NIC 後兩碼**（`esp_read_mac` 的 `mac[4],[5]`），不要用 `getEfuseMac()&0xFFFF`（OUI 前綴，全部裝置會撞成同一個 ID）。

### 11.3 實機觀測 / 測試環境

8. **ESP32-C6 原生 USB（HWCDC）的 serial 輸出，重置後 host 端會短暫斷線**，PC 不易穩定讀到開機 log。**建議改用 WiFi 遙測**（`GET /version` 走 TCP，可靠）做實機除錯，或在 `loop()` 放週期心跳。
9. **Windows 防火牆會擋掉回傳的 UDP**，導致從 PC 測 UDP 像「卡死」其實是收不到回應；**PC 端診斷請用 HTTP/TCP**。手機連 WiFi 不受此影響。
10. **多台同時測試**：softAP 預設同網段 `192.168.4.x`，若 PC 有兩張無線網卡同時連上兩台會路由衝突；多機測試建議**用手機**、或讓裝置改不同網段、或用裝置自身（OLED/LoRa 統計）觀測。
11. **未號數時間比較**：保留畫面（如 SOS 警示）勿用 `now - (millis()+N)` 無號數比較（會下溢立即失效），改用 `(int32_t)(holdUntil - now) > 0`。

### 11.4 已知硬體/待辦

- **OLED 偶發開機不亮屬暫態**（曾見 BF8C 開機不亮，重新上電/重燒後恢復；兩台 OLED 現均正常），非硬體缺陷；若遇到先重上電或重燒。
- **SOS 三連按**偵測時序可再調校。
- 雙機 LoRa 收發、SOS 端到端**已實機驗證通過**（RSSI -27、Rx 累加）；兩台 OLED 皆正常。

### 11.5 Android App bring-up 注意事項（S25 實機）

> 手機 App 端到端（手機→WiFi→C6L→LoRa→另一台）已實機驗證通過。過程踩雷：

1. **Blazor 路由衝突**：兩個頁面同為 `@page "/"`（範本殘留 `Index.razor` 與自訂首頁）→ Router 排序拋例外 → App 卡在 Loading。需移除重複路由（並清掉 Counter/FetchData/SurveyPrompt 等範本殘留）。
2. **Codec2 native library 未編譯**：首頁若注入 PTT 的 `MainViewModel`→`Codec2Service`，收到資料呼叫 `Decode` 會 `DllNotFoundException` 崩潰。對策：文字頁設為首頁、解碼包 try/catch、僅通話中解碼。
3. **Android「無對外網路的 WiFi」路由**：C6L 的 AP 沒有對外網際網路，Android 預設會把 UDP/TCP 送往行動網路 → 連不到 192.168.4.1。**對策：`ConnectivityManager.BindProcessToNetwork` 綁定到 WiFi 網路**（NetworkRequest 要 `RemoveCapability(Internet)`），manifest 需 `CHANGE_NETWORK_STATE` 權限。
4. **OTA http 明文**：Android 9+ 預設擋 cleartext，OTA 的 `http://192.168.4.1/update` 需 manifest `usesCleartextTraffic="true"`。
5. **adb 安裝 Debug APK**：Debug 預設 Fast Deployment 不把 assemblies 打包進 APK，直接 `adb install` 會 `monodroid: No assemblies found → SIGABRT`。需 `-p:EmbedAssembliesIntoApk=true` 或用 VS 部署。
6. **實機除錯**：MAUI `ILogger`(AddDebug) 不一定進 logcat；改用 `Console.WriteLine`（顯示為 logcat `DOTNET` tag）。手機連無網路 WiFi 時系統 NetworkMonitor 會狂刷 logcat 把日誌擠掉，需用 `adb logcat -s DOTNET` 過濾。
7. **連線冪等**：`ConnectAsync` 應在已連線時直接返回，避免重複建立 UdpClient 把接收迴圈弄死。
