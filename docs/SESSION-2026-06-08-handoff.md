# 工作階段交接紀錄 — 2026-06-08

> 本檔為當日所有變更、決策、踩雷與待辦的單一真實來源（single source of truth）。
> 回到任何機器 `git pull` 後，先讀此檔再繼續。**不要走回頭路**——已標註的結論勿重新推翻。

裝置：CDB8（COM6, MAC `20:6e:f1:15:cd:b8`）、BF8C（COM7, MAC `20:6e:f1:15:bf:8c`）

---

## 一、本次已完成並實機驗證通過的修正

### 韌體（firmware/，兩台 CDB8/BF8C 皆已燒錄）

1. **HWCDC 非阻塞（根因級修正）** — `main.cpp` setup() + `usb_serial_service.cpp`
   `Serial.setTxTimeoutMs(0)`。USB 插著供電但沒程式讀序列埠時，TX 緩衝區塞滿會
   **卡死整個 main loop**（loops 計數從 ~50 萬掉到卡在 75），導致按鈕漏抓、OLED 不重繪、
   WiFi/HTTP 反應遲緩。設 0 後無人讀就丟棄、不阻塞。
   → 連帶解決了「按鈕看似失效」「WiFi 很不穩」**兩個原本被誤判為各自獨立的問題**。
   並移除 `led.cpp` 每次換色都印 Serial 的熱路徑 log。

2. **按鈕本身沒壞** — 之前以為按鈕/I2C/M5Unified 有問題，全部排除。
   實證：IO 擴充晶片 reg 0x0F = `0x03`(未按)/`0x02`(按下)，`M5.BtnA` 邊緣偵測正常。
   真凶就是上面的 HWCDC 卡迴圈。`/version` 已加按鈕偵錯欄位（`btn_reg`/`wp`/`wr`/`sc`/`tc`/`loops`）。

3. **RGB LED 驅動（原本是空殼）** — `led.cpp`
   原 `_set()` 的 `M5.dis.drawpix` 被註解掉，從沒驅動過燈。
   **Unit C6L 的 WS2812C RGB LED 接在 GPIO2**（由 M5Unified 板表確認）。
   改用 ESP32 核心內建 `neopixelWrite(2, r,g,b)`，整體亮度 `LED_BRIGHT=40`。已實機亮燈驗證。

4. **連續接收（取代 RxDutyCycle）** — `power_mgr.cpp`，`#define ENABLE_RX_DUTYCYCLE 0`
   原待機用 RxDutyCycle（醒5ms睡995ms），但喚醒前導碼只有 16 symbols(~4ms)，蓋不住
   1 秒睡眠窗 → 冷開機第一個封包（含 SOS、PING）被睡過去，且「收不到→不醒→永遠收不到」死結。
   Phase 1 可靠性優先，改全程連續接收。**要重新啟用省電 RxDutyCycle，必須同步把喚醒前導碼
   加長到約 4000 symbols（≈1 秒空中時間）才行**——這是重啟用的前提，別忘了。

5. **PING 回覆隨機退避** — `main.cpp` `replyPing()`，`delay(30 + esp_random()%120)`（30~150ms）
   廣播 PING 時，被探測方會「先轉發回音、再回覆」兩次背靠背發射，探測方抓到回音、正忙著
   處理/透過 WiFi 推給 APP 時會漏掉緊接的回覆。退避讓探測方先處理完回音再收回覆；
   多節點時也錯開彼此回覆。**必須用 `esp_random()`（硬體亂數），不可用無種子的 `random()`
   （各台會挑到相同延遲照樣互撞）。**

6. **中繼排除自己的封包** — `relay.cpp` process() 開頭 `if (pkt.srcId == _myId) return false;`
   自己發出的封包被他人中繼「原封彈回」後，原本會被自己當成別人的訊息處理 →
   造成「APP 探測到自己」「APP 收到自己廣播的訊息(0xCDB8 廣播 …)」假象。標準 mesh split-horizon。

### APP（app/LoRaPTT/，已 build 並安裝到 S25）

7. **搜尋附近裝置改為 dialog + 挑選新增 + 排除自己** — `ChatViewModel.cs` / `ChatPage.razor`
   - 探測結果進**暫存清單 `DiscoveredDevices`**（不再自動塞進持久化聯絡人）。
   - 搜尋完跳**置中 dialog**「探測到的裝置」，每台有「**新增**」鈕，點了才加為聯絡人；
     已是聯絡人顯示「已加入」。
   - **排除自己是用 DeviceId 比對**（`contact.DeviceId == LocalDeviceId`），**不是用名稱**。
     ⚠ 重要結論：兩台都叫 "LoRaPTT" 也不會互相排除，名稱完全不參與判斷。
   - `TouchContact` 也加了「排除本機自己」保險。

---

## 二、待辦（尚未實作，下次接續）

1. ~~**搜尋連發多次 PING（提高探測可靠度）**~~ ✅ **已實作（2026-06-08，家裡筆電）**
   `ChatViewModel.PingAsync()` 改為 5 秒掃描窗內連發 3 次 PING（t≈0/1.5/3 秒）；
   重複回覆依 DeviceId 去重（OnDeviceDiscovered），清單只顯示一台。
   ⚠ **尚未 build/實機驗證**：家裡筆電僅有 net7 SDK，無法 build net10-android。
   **下次在 net10 機器（公司）build + 走 uninstall→install --no-incremental 流程實測**。

2. **(選配) 搜尋結果把裝置 ID 顯示更明顯** — 因預設暱稱都叫 "LoRaPTT"，清單只能靠 ID 區分。
   建議使用者在設定頁給每台取不同暱稱（暱稱會寫進 PING 回覆）。

3. **(後期) 重新啟用 RxDutyCycle 省電** — 前提：喚醒前導碼加長到 ~4000 symbols（見一-4）。

4. **(後期構想) 與 Meshtastic 生態互通 — App 翻譯橋(閘道層,非 RF 層)**
   - 概念：手機 App 同時連「一台刷成 Meshtastic 的裝置(BLE/序列, 用其公開 protobuf API)」+「自家 C6L」，
     App 當雙語翻譯：他們 protobuf ↔ 我們封包格式，雙向轉。
   - **只翻文字/相容訊息；語音不過**(Meshtastic 網路不支援語音；RF 參數也互斥 SF7/BW500 vs LongFast)。
   - 橋接發生在**手機**(唯一接點)，兩個 LoRa 網路 RF 上仍分開；需一台實體 Meshtastic 節點當入口。
   - 需做 ID/定址映射表(他們 4-byte node number + channel/PSK ↔ 我們 2-byte device ID)。
   - **不動 C6L 韌體、不重寫 RF**；純 App 端新增 `MeshtasticCommService`(與現有 ICommService 抽象平行掛上)。
   - 排程：**等語音 PTT(Phase 4)與搜尋可靠度收尾後再做**；延後零架構代價。手邊多的硬體可隨時刷一台
     Meshtastic 並行實驗/學習，但不在關鍵路徑。

---

## 三、建置/部署踩雷（務必照做，否則白忙一場）

### 韌體
- 建置/燒錄用 ASCII junction `C:\LoRaPTT_Build`（→ 專案主目錄；中文路徑會出問題）。
- 燒錄前設 `$env:PYTHONIOENCODING="utf-8"`，再 `pio run -t upload --upload-port COM6`（或 COM7）。

### APP（.NET MAUI Blazor，net10.0-android）
- **aapt2 不支援非 ASCII 路徑**：專案真實路徑含「LoRa通訊」，直接 build 會 `APT2265` 失敗。
  **必須在 PowerShell 從 junction `C:\LoRaPTT_Build\app\LoRaPTT` 建置**（PowerShell 不解析 junction）；
  **不要用 Git Bash 的 `cd`**（它會把 junction 解析回中文真實路徑 → 失敗）。
- **Fast Deployment 快取陷阱（重要）**：手機殘留舊的 Fast Deployment 組件時，即使裝了內嵌組件的
  新 APK，**APP 仍會載入舊快取組件 → 看起來「沒更新」**。
  解法：**先 `adb uninstall com.companyname.loraptt`，再 `adb install --no-incremental <signed.apk>`**。
  （`adb install -r` 的 incremental 安裝清不掉快取。）
- build：`dotnet build .\LoRaPTT.csproj -f net10.0-android -c Debug -p:EmbedAssembliesIntoApk=true`
  （`EmbedAssembliesIntoApk=true` 必加，否則 Fast Deployment 找不到組件 → SIGABRT）。
- 驗證 APK 真的含新碼：解壓 APK → `lib/arm64-v8a/lib_LoRaPTT.dll.so`，搜成員名(UTF-8)即可。

### 本機環境（家裡筆電會不同，需自行對應）
- adb：`C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe`（未在 PATH）。
- 手機：Galaxy S25（`SM-S9360`），adb serial `R5GL22P3KGV`，package `com.companyname.loraptt`。
- C6L：CDB8=COM6、BF8C=COM7；WiFi AP 預設 `LoRaPTT_<ID>`，HTTP 遙測 `http://192.168.4.1/version`。
- 韌體版本 FW 0.4.0；APP 版本 1.0（versionCode 1）。

---

## 四、給下一個工作階段的提醒
- 確認改動方向前先看本檔「待辦」與各結論標註，**已排除的方向（按鈕硬體、名稱排除）勿重查**。
- 改 APP 後務必走「uninstall → install --no-incremental」流程，否則會誤判「沒更新」。
- 任何改完依專案規則 commit + push。
