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

---

## 五、2026-06-08 晚間續做（家裡筆電，已全部 push 到 35e4c79）

### 🔒 最重要：工具鏈版本已鎖定（別再升版！）
- 白天公司那次 `75df490` 把專案從 **net7 升成 net10 卻沒鎖 SDK** → 害「公司能 build、家裡不能」、晚上白耗數小時。
- 已修正並鎖死：**csproj 退回 net7**、repo 根新增 **`global.json` 釘 SDK 7.0.400**、CLAUDE.md 協作規則第 4 條加「禁止升降版」硬規則。
- **明天到任何機器：pull 後該機需有 `.NET SDK 7.0.400` + net7 maui-android workload（JDK11 + Android API33）。若某機只有新 SDK(10)，正解是補裝 7.0.400，不是升專案**（新 SDK 會以 NETSDK1202 拒 build net7）。
- APP build 指令（家裡筆電路徑 `C:\Dev\LoRaPTT` 為純 ASCII，免 junction）：
  `dotnet build app\LoRaPTT\LoRaPTT.csproj -f net7.0-android -c Debug -p:EmbedAssembliesIntoApk=true`

### 已完成並實機驗證（用 adb 自動驅動 S25 截圖/ logcat 驗收）
1. **搜尋連發 3 次 PING**（`ChatViewModel.PingAsync`，5 秒窗 t≈0/1.5/3s）→ 連跑 3 輪、每輪都穩定找到 BF8C（logcat 見多筆回覆、依 DeviceId 去重）。**「搜尋時有時無」已解。**
2. **ChatPage 發送目標列改兩排**：目標 badge 放大佔滿寬度（👤/📡）＋管理靠右；廣播/選擇對象/ID 收下排。純版面、邏輯不動。
3. **權限白名單**（`.claude/settings.local.json`，gitignore、僅本機）：放行整個 PowerShell + git/rm/ls/grep/tail/cat/dotnet + Edit/Write/Read → build-deploy-test 全自動、免按 allow。

### 本機自動化驗收流程（可重用）
- 手機 WiFi 已連 `LoRaPTT_CDB8`（192.168.4.2，AP=192.168.4.1）；兩台 CDB8/BF8C 都開著、RSSI 約 −30。
- 開 App：`adb shell monkey -p com.companyname.loraptt 1`
- 截圖：`adb shell screencap -p /sdcard/s.png; adb pull /sdcard/s.png`（Blazor WebView 的按鈕 uiautomator 看不到 → 用截圖算座標 `adb shell input tap X Y`，座標用實機 1080×2340）
- 看 App log：`adb logcat -s DOTNET:*`（tag DOTNET；無網路 WiFi 時別開 NetworkMonitor 洗版）
- ⚠ 首次裝新版可能跳「16KB ELF 對齊」相容性警告（net7 lib，無害）→ 點「不要再顯示」。

### 待辦（明天起點，依優先序）
1. **Phase 4 語音 PTT**：唯一大項。需編 **Codec2 native lib（Android arm64-v8a，要 NDK/CMake）** → P/Invoke。這是唯一有「環境步驟」的部分，謹慎做、別讓它變第二次繞環境。`MainViewModel` 的 `Codec2.Decode` 已有 try/catch，僅通話中解碼。
2. 其餘 UI/UX 續修（PTT 頁、設定頁），同樣用上面的 adb 自動截圖驗收。
3. （後期）RxDutyCycle 省電（前提：喚醒前導碼加長 ~4000 symbols）。
4. （後期構想）Meshtastic 互通 App 翻譯橋。

> 明日 08:00 續。pull 到最新 `main`（HEAD=35e4c79 或更新），先讀本節「五」。

---

## 六、2026-06-09 net7 build 環境踩雷（換機器照這份補裝，別再卡）

git 基準鎖 net7 + SDK 7.0.400。一台「只有新 SDK」的機器要能 build net7-android，需依序補齊：
1. **`.NET SDK 7.0.400`**（`winget install Microsoft.DotNet.SDK.7` → 裝到 7.0.410，`rollForward:latestFeature` 接受）。
2. **maui workloads**：`dotnet workload restore app\LoRaPTT\LoRaPTT.csproj`（會裝 android+ios+maccatalyst；只裝 maui-android 會在評估其他 TFM 時報 NETSDK1147）。
3. **Android SDK Platform API 33**（net7 maui 預設 target；機器可能只有 34/35/36）。安裝雷：
   - 寫入 `C:\Program Files (x86)\Android\android-sdk` 需 **系統管理員**（UAC 提權）。
   - **新版 `cmdline-tools\latest\bin\sdkmanager` 需 JDK17+**；**舊版 `tools\bin\sdkmanager` 需 JDK8（要 JAXB）**。本機只有 JDK11 → 兩者都掛。
   - ✅ 正解：用**舊版 `tools\bin\sdkmanager.bat` + `JAVA_HOME` 指向 JDK8**（本機 `C:\Program Files\Eclipse Foundation\jdk-8.0.302.8-hotspot`）裝 `"platforms;android-33"`，成功。
   - **依規則：以上一律「補裝環境」，絕不改 csproj 的 target API/TFM 來遷就。**
- build：`dotnet build app\LoRaPTT\LoRaPTT.csproj -f net7.0-android -c Debug -p:EmbedAssembliesIntoApk=true`（從 ASCII junction `C:\LoRaPTT_Build`，PowerShell）。

### 本日（06-09）已完成並 ✅ 實機驗收通過（S25）
- **A**：收到 SOS → 聊天室插「🆘 來自 0xXXXX + 定位(有的話) + 附加文字」（`ChatViewModel.OnSosReceived`）。✅
- **B**：`AndroidManifest` 補 `ACCESS_COARSE/FINE_LOCATION`（修 SOS 頁定位失敗，定位權限正常）。✅
- 仍待辦：群組/點對點/OTA 補實機驗證、F-051 設定頁、Phase 4 語音。

---

## 七、2026-06-09 PC 客戶端 + 雙向實測通過

- **PC 客戶端走 WiFi**（`pc-client/`，net7 console，重用協定碼）。USB CDC 在原生 HWCDC 上對會開埠的 host 不穩 → **改 WiFi（與手機同通道，穩定）**。
  - 用法：PC 的 WiFi 先連目標 C6L 的 AP，再 `dotnet run --project pc-client -- wifi 192.168.4.1 FFFF`。
  - 測試參數：`send:<文字>`（送 3 次避單發遺失）、`secs:<N>`（probe 監聽秒數）、`probe`（非互動）。
  - netsh 連 AP：建 WLAN profile（WPA2PSK, key `loraptt2026`）→ `netsh wlan connect name=LoRaPTT_BF8C`。
- **雙向實測通過**（手機 CDB8 ↔ PC BF8C，PC 當第二端點，解掉單手機死結）：
  - PC→手機：手機聊天室顯示「0xBF8C 廣播 PC2Phone」✅
  - 手機→PC：PC 印出 3× `[0xCDB8→0xFFFF] PING`（RSSI -33）✅
- **關鍵結論**：**單發 LoRa 封包會間歇遺失，送 3 次才穩**（廣播文字/PING 皆然）。
  → ~~待辦：廣播/群組文字也應比照探測，送多次~~ ✅ **已實作（06-09 家裡筆電）**：
  `MessagingService.SendTextAsync` 廣播/群組改**同 SEQ 重送 3 次（間隔 200ms）**，
  接收端依 SRC+SEQ 去重收成一則；點對點維持單發（有 ACK）。
  已實機驗收：S25 發廣播文字只顯示一則、✓ 廣播不回 ACK、無自收、App 穩定。
  ⚠ 尚未量測 far-side 掉包率（筆電收不到 C6L AP，無法當第二端點）；待裝置靠近筆電
  或用手機+PC 兩端時補。
- 共用核心 lib 尚未正式抽出（PC 端目前用 `<Compile Link>` 重用 Protocol 檔）；之後可抽 `LoRaPTT.Core`。

---

## 八、2026-06-09 晚 新增第三台節點 D400（已燒＋驗收）

- 新機（空 NVS）燒入 git 最新韌體 **FW 0.5.0**（家裡筆電 `pio run -e m5stack-unitc6l -t upload`，COM9）。
- **Device ID = 0xD400**（MAC 尾碼 `20:6E:F1:15:D4:00`）；SSID `LoRaPTT_D400`、密碼 `loraptt2026`（全機通用預設）、AP BSSID `d4:01`（NIC+1）。
- 燒完 esptool 的 RTS reset／軟體 DTR 都沒讓 app 乾淨起來（HWCDC 老問題）→ **手按一下 RESET 才正常開機**；OLED 顯示 `ID:D400 / LoRaPTT / APP:none`、AP 廣播 RSSI −22。**結論：新機燒完務必手動 RESET 一次再驗證。**
- 目前共三台：**CDB8、BF8C、D400**，可做中繼/多跳/群組測試。
- 待定：D400 要當第三端點還是中繼節點（明天決定後再規劃驗證）。

> 明日（06-09 之後）在公司續：先 `git pull`（HEAD≥`b4b2f27`），讀本檔第五~八節決定方向。
> 鎖定規則照舊：net7 + SDK 7.0.400，編譯問題只補環境、不改專案版本。

---

## 九、F-054 USB 半通修復 — ✅ 實機驗證通過（FW 0.5.2）

- **症狀**：手機 USB OTG 接 C6L，App 永遠卡「握手中」（hello 送得到、ack 回不來＝半通）。
- **根因**：`usb_serial_service.cpp` 的 `send()` 沿用全域 `setTxTimeoutMs(0)`——host（Android USB 驅動）沒「即時就緒」時，**ack 寫入被直接丟棄**，手機收不到。`g_usbDataMode` 要下一圈 loop 才設 true，送 ack 當下沒保護。
- **修法**（commit `45f1038`，FW 0.5.2）：`send()` 只在「對方是已握手 APP」時被呼叫，故**寫入期間設 `setTxTimeoutMs(50)` 確保送達、寫完還原 0**（保留 power-only 純供電時 debug log 不卡 main loop 的原始保護）。**非更底層 HWCDC 問題，不需動 TinyUSB。**
- **驗證**：D400 燒 0.5.2 → 手機 USB OTG 接 D400 → App **「已連線」** ✅。
- **踩雷紀錄（debug 流程經驗）**：
  - 手機做 USB OTG host 時，**手機唯一 USB-C 被佔住 → adb 斷線**，筆電讀不到手機 log。改用 C6L 端觀測。
  - C6L 端遙測：筆電 WiFi `netsh wlan connect name=LoRaPTT_xxxx interface="Wi-Fi 2"`（**雙網卡必須指定 interface**，否則連不上）→ `GET http://192.168.4.1/version`。零改動、零接線，比序列 log 好用。
  - 每次改韌體要重燒 D400：得把 D400 USB-C 從**手機**換回**筆電**（COM9）→ 燒 → **手動 RESET** → 換回手機測。USB-swap 來回很煩，但目前無解（除非裝 Grove UART debug 線）。
- **完整端到端實機驗證通過**（USB 收訊那一哩也補上了）：
  `筆電 →(WiFi)→ BF8C(0.5.2) →(LoRa)→ D400 →(USB OTG)→ 手機App 顯示`，
  且點對點（DST=D400）D400 有**回 ACK**（pc-client 收到 `[0xD400→0xBF8C] type=0x04`，RSSI −24）、手機 App 也顯示「0xBF8C → Hello-D400」。
  → 證明 USB OTG 雙向（hello-ack + 收 LoRa 推播）皆正常。
  - pc-client 送出務必加 `probe`（否則跳過 send、卡互動 ReadLine、`tx` 永遠 0）。
  - 筆電 WiFi 連 C6L 要指定 `interface="Wi-Fi 2"`；多 AP 同為 192.168.4.x，一次只連一台避免衝突。
- **版本現況**：**三台齊一 = FW 0.5.2**（CDB8 / BF8C / D400 皆已升並實機驗證）。
  CDB8→D400 點對點：D400 每收一份回一個 ACK（×3，RSSI −25~−27），手機 App 顯示「0xCDB8 → …」。

---

## 十、2026-06-13 假日：GPS 整合 + USB 拔線修正（多台到位）

### 裝置現況（共 4 台）
- **CDB8、BF8C、D400、D078**（新燒 D078=ID 0xD078）。FW：**BF8C=0.6.0（含 GPS）**，CDB8/D400/D078=0.5.2（無 GPS 模組，未急升）。
- 互發實測：CDB8/BF8C/D078 → D400 點對點皆通、D400 回 ACK、手機顯示。

### GPS 整合（M5 GPS Unit U032 / AT6668）— ✅ 實機定位成功
- **接法**：M5 原廠 Grove 排線插 C6L Grove PORT.A。**GPS TXD→C6L GPIO4(RX)**、RXD→GPIO5(TX)。
- **關鍵雷：U032 出廠鮑率是 `115200`**（非常見 9600/38400）。錯鮑率下 framing error 把資料丟光、`gps_bytes` 幾乎不動 → 誤判沒接。
- 韌體 `gps.h/.cpp`：Serial1 讀 NMEA、解析 GGA，**自動輪試 4 腳位×4 鮑率(8 組合)**，看到 `$…*` 合法句才鎖定。實機鎖定 **GPIO4@115200**，定位成功（北緯 25.19、東經 121.44、8~9 衛星）。
- `/version` 增 `gps_fix/gps_sats/gps_lat/gps_lon/gps_bytes/gps_baud/gps_rx`，WiFi 即可遙測定位。
- **SOS 接 C6L GPS**（FW 0.6.0）：實體按鈕 SOS 有 fix 時 payload 附 `[Lat 8B][Lon 8B]`（小端，對齊 APP `OnSosReceived` lat@2/lon@10）→ **不靠手機也能報座標**。實機：BF8C 連按 3 下 → 手機 SOS 警報帶出座標 ✅。
- **OLED GPS 頁**（第 5 頁，短按輪到）：`GPS FIX / sat:N / 緯度 / 經度`。64×48 每行約 10 字，6 位小數座標**剛好一行放得下**（有 `lat:` 前綴就會超寬，故座標獨佔整行、不加前綴）。實機顯示完整不截字 ✅。
- 觸發 SOS = **連按按鈕 3 下**（F-071，`Button::onTriplePress`）。

### USB 拔線偵測修正（APP，commit `8a61b2f`）— ✅ 驗證通過
- **症狀**：手機拔掉 USB 裝置後 App 仍顯示「已連線」。
- **根因**：`UsbSerialImpl.ReadLoop` 的 `BulkTransfer` 拔線時回 ≤0，原碼只 `continue` 永不結束 → 斷線事件不發。
- **修法**：`ConnectAsync` 註冊系統 `USB_DEVICE_DETACHED` 廣播 → 拔線即 `HandleDisconnect()`（標記未連線、取消讀迴圈、發 `OnConnectionChanged(false)`）。ReadLoop finally / DisconnectAsync 統一走 HandleDisconnect。
- 驗證：拔掉→「未連線」、接回按連線→可連 ✅。

### 後期（backlog，未做）
- **定期廣播自身 GPS 位置**（節點互相在地圖上看到彼此）：用 TYPE 感測/位置封包定時廣播 lat/lon。等核心穩定後再做。
- CDB8/D400/D078 視需要升 0.6.0（目前無 GPS 模組，非必要）。

### ⛔ PC 端診斷鐵則（2026-06-13 又踩一次，務必遵守）
- **驗證某台 C6L「有沒有收到 LoRa」一律用 HTTP `GET http://192.168.4.1/version` 的 `rx`/`src`/`rssi`（TCP）**。
  rx 有跳、src 是發送方 = 收到。這是唯一可靠的 PC 端收訊判斷。
- **絕對不要用 `pc-client` 的 UDP 監聽來判斷「收訊」**：Windows 防火牆**擋裝置→PC 的入站 UDP**，
  pc-client 連 hello-ack 都收不到、收到的 LoRa 也推不進來 → **會誤判成「什麼都沒收到」**（本日就因此白繞一大圈）。
  pc-client 的「**送出**」(outbound) 不受影響，仍可用來發訊。
- 要讓 pc-client 監聽可用 → 需**系統管理員**加防火牆入站規則：
  `netsh advfirewall firewall add rule name="LoRaPTT-UDP-in" dir=in action=allow protocol=UDP remoteip=192.168.4.0/24`
- **另：每台燒完/重開後要等它「跑穩」**（OLED 有畫面、`/version` 的 loops 正常爬升）**再測**；
  剛重開的暫態會 rx=0、收不到，別誤判成壞掉。

### Mesh 可靠性修正（2026-06-13，✅ 實機驗證）
- **發送端 SEQ 隨機起始**（`MessagingService._seq` / pc-client `seqCounter`）：原本每次開 App/執行都從 1 開始，
  重啟後送的 `(SrcId,Seq)` 與接收端去重快取的舊紀錄相同 → **被當重複丟棄、訊息不顯示**（CDB8 有響=有收到，
  但手機去重擋掉）。改隨機高起始值即解。
- **pc-client 送 3 份改用同一 SEQ**（對齊 App `SendTextAsync`）：原本各份不同 SEQ → 接收端不去重 → 同訊息顯示多次。
- **🔑 relay 轉發隨機退避 0~50ms（`relay.cpp`，FW 0.6.1）**：密集部署(多台在直連範圍內)時，每則廣播會讓所有
  中繼「同時」轉發 → SX1262 半雙工互撞、把訊息含重送副本一起蓋掉。實測 **4 台桌面：加退避前「3 則只到 1 則」、
  加退避後「5 則全到」**。`esp_random()%50` 把各台轉發時間錯開。
  → **越擠越糟**（與「拉開測多跳」直覺相反）；退避是密集網路的關鍵改善，對真實部署也有用。
- 版本：四台皆 **FW 0.6.1**。

### 中繼 + 雙向 live 驗證（2026-06-13，✅ 完整實機）
- **雙路同時監聽**（好用，記下來）：D078 走 **WiFi**（HTTP `/version` 讀 rx/src）、D400 走 **USB**（讀序列 HB/`[Relay]` log）。
  ⚠ 開 D400 USB 序列會**重置它一次**（HWCDC），要等它開機穩定(~10s)再發測；序列穩定後即可即時看到 `[Relay] 轉發`。
- **中繼證實**：手機(CDB8) 發廣播 → D400 序列印 `[Relay] 轉發 SRC=0xCDB8 DST=0xFFFF HOP=4`、HB `fwd` 累加 → D400 確實接力轉發（HOP 遞減）。
- **雙向 + 內文 + ACK**：手機→「d078」→ D078，pc-client(當 D078 端點)讀到內文「d078」並回 ACK×3（手機顯示✓✓）；
  D078→「D078-REPLY-OK」→ 手機顯示。**雙向訊息、ACK、中繼接力全部跑通。**
- pc-client 監聽(入站 UDP)**時通時擋**：防火牆 stateful 有時放行回程、有時擋；要穩定需管理員加入站規則（見上）。

### 🔑🔑 最大根因：C6L WiFi 單 client → 已改多 client（FW 0.6.2）
- **症狀**：手機/pc-gui「斷斷續續、收不到、握手當下能收之後就沒」。耗一整天才揪出。
- **根因**：原 `wifi_service` 只記「**最後一個傳 UDP 給它的 client**」（`_clientIp/_clientPort`），
  收到 LoRa 只推那一個。**多支 client（手機+pc-gui+pc-client）連同一台 AP 時互相搶推送對象**
  → 後連的搶走、先連的就收不到。**debug 期間反覆跑 pc-client 連同一台 C6L = 一直把手機/pc-gui 的
  收訊搶走**（自己製造的假象，浪費大量時間）。
- **修法**（commit `f92f705`，FW 0.6.2）：`wifi_service` 改 **client 清單**（MAX 4、LRU 淘汰、5 分鐘 TTL）；
  收到封包就記進清單，`send()` **推給清單裡全部 client**。多支裝置連同一台 AP 都收得到、不互搶。
- **狀態**：CDB8/BF8C/D400/D078 **四台全燒 0.6.2**。
- **⚠ TTL 的副作用（必記）**：多 client 清單 **5 分鐘 TTL，只認 UDP port 5000 的活動**。
  client 閒置不送 UDP → 5 分鐘被剔除 → 收不到，要再送東西(或重按連線)才恢復。
  **HTTP `GET /version`(TCP) 心跳不算數**（不同 socket，不更新 UDP 註冊）。
  → 解：每個 client 週期送 **UDP keepalive**（1 byte `0x00`）：pc-gui 加 60s Timer；
  手機 App 在心跳迴圈(每 4s)順帶送 UDP。Windows 端同時也保住防火牆 UDP 回程映射。
- **debug 鐵則補充**：診斷時**別把 pc-client/HTTP 以外的 UDP client 連到「手機正在用的那台 C6L」**
  （舊韌體會搶槽）；要嘛用多 client 的 0.6.2，要嘛連「不同的節點」。
- **附帶**：S25 可同時當 C6L 的 WiFi client + 開熱點給 PC → PC adb-over-wifi 連手機(`adb connect <hotspot-gw>:5555`)，
  能驅動手機又不佔 C6L 的 client 槽，是乾淨的觀測法。
