# 交接 — 2026-06-15（NodeDB + 戰術地圖 + 離線地圖大進展）

> **回家第一件事：`git pull`**（HEAD 應為 `89aa09d` 或更新）。本機 build/flash 細節見 CLAUDE.md。
> 三台 C6L（CDB8/BF8C/D400）都已燒 **FW 0.7.5**、可互通（D078 未更新）。手機 R5GL22P3KGV 已裝最新 APK；**家裡電腦要測得自行 rebuild + adb 部署**。

## 一、本 session 成果（皆已 commit+push）

### 韌體 FW 0.7.5
- 第1頁顯示 FW 版本 + `WiFi:ON/OFF`（F-040/F-064）
- WiFi 閒置 5 分自動關（F-045，有 station 連著不關）、螢幕閒置 20 秒關 + 收發/按鍵喚醒（F-044）
- 定位定時/智慧廣播 `TYPE=0x07`（F-074，移動才提早發）、NodeDB 節點資料庫（F-036）

### APP（.NET MAUI Blazor）
- 節點頁（F-036，從收到封包自動建檔）、定位 0x07 解析、設定頁 `pos_interval`（F-051）
- **F-054 USB 其實早就修好且驗證過**（之前以為壞，是「沒先 git pull、在過時 repo 上鑑識」的假警報）
- **地圖 F-037**（這次最大塊）→ 見第二節

## 二、地圖 F-037 架構（續做前務必看懂）
- **引擎**：MapLibre GL JS（本地打包於 `wwwroot/lib/maplibre/`），嵌 Blazor WebView，一套跨 Android/iOS。互通在 `wwwroot/js/map-interop.js`、頁面 `Pages/MapPage.razor`
- **底圖（皆 NLSC 官方，走本機協定 `loraoff://{layer}/{z}/{x}/{y}`）**：
  - 街道 = NLSC **EMAP**；衛星 = NLSC **PHOTO2**
  - 協定 handler → 呼叫 .NET `[JSInvokable] GetOfflineTile(layer,z,x,y)`：本機有→回本機（離線可用）；無且有網路→抓 NLSC 並快取；都無→空白
- **氣象雷達** = CWA `O-A0058-005` 透明整合回波（MapLibre image source 疊加；範圍 經 115.00–126.50 / 緯 17.75–29.25）。**S3 圖檔無 CORS → 由 .NET 原生 HTTP 抓回轉 data URL** 餵地圖
- **離線下載**：右上「下載此區」鈕 → 抓目前畫面（目前縮放 ~z16）的**衛星+街道兩層** → 存 `AppData/offlinetiles/{layer}/{z}/{x}/{y}.jpg`（`Services/OfflineTiles`）→ 多次下載自然累積覆蓋、重下載=覆蓋更新。底部半透明進度條 + 取消。設定頁顯示容量 + 清除
- **UI**：右上鈕 = 回到自己(oi-map-marker)/節點清單(oi-people)/底圖切換(oi-layers)/雷達(oi-rain)/下載(oi-cloud-download)；右下半透明資訊浮卡（自己或點選節點）

## 三、待做（回家接續）
- **地圖 v2 剩**：③ 線上↔離線**手動切換鈕**（強制離線、省行動數據）、④ MBTiles 化（低優先，目前檔案方式夠用）
- **語音（使用者指定的下一個大工程）**：Codec2 native lib **沒編譯**（F-055）→ 要 **Android NDK 交叉編譯 `libcodec2.so`（arm64-v8a）** 放 `app/libs/codec2/android/`，PTT 的 `Codec2.Decode` 才不會 DllNotFound；iOS 音訊 F-056 為 stub
- 其他 backlog：F-035 管理式洪泛、離線訊息暫存…（見 SPEC）

## 四、本 session 踩雷／關鍵技術點（省得回家再踩）
- **MapLibre GL v4 的 `addProtocol`**：handler 必須 `async`、回傳 `{ data: ArrayBuffer }`（v4 移除了舊 callback 介面；用 callback 會「圖磚永遠載不出」——衛星空白就是這個）
- **NLSC WMTS 圖磚順序是 `{z}/{y}/{x}`**（不是 x/y）；圖層 衛星=PHOTO2、街道=EMAP（皆 curl 驗證 200）
- **CWA 雷達 S3 無 CORS**：MapLibre image source 直接載會 tainted → 改 .NET 抓圖轉 data URL
- **線上地圖一定要網路**：手機連 C6L 的 WiFi AP（無對外網）時圖磚 `Failed to fetch` 載不出 → 測線上地圖要「USB 連 C6L（手機 WiFi/4G 空出）」或一般有網路 WiFi；**離線打包正是為了 C6L WiFi/無訊號災區**
- **開工務必先 `git pull`**（本 session 曾因沒同步、在過時本機 repo 上做鑑識而誤判「0.7.0 遺失」嚇到使用者——切記）

## 五、build / deploy 速記（詳見 CLAUDE.md）
- **APP build**：從 ASCII 路徑用 **PowerShell**（**勿用 Git Bash**，中文路徑 `LoRa通訊` 會觸發 aapt2 APT2265）。本機是 junction `C:\LoRaPTT_Build`；**家裡電腦若 repo 在中文路徑，先 `mklink /J` 一個 ASCII junction 再 build**
  - `dotnet build LoRaPTT.csproj -f net7.0-android -c Debug -p:EmbedAssembliesIntoApk=true`
- **部署**：`adb uninstall com.companyname.loraptt` → `adb install --no-incremental <...-Signed.apk>`（不 uninstall + EmbedAssemblies 會 Fast Deployment 找不到 assembly → SIGABRT）
- **韌體**：`$env:PYTHONIOENCODING="utf-8"; pio run -t upload --upload-port COMx`（CDB8=之前 COM6、BF8C=COM7、D400=COM8，依實際枚舉為準）
- **版本鎖**：APP `net7.0-*` + .NET SDK 7.0.400/410，**勿升 net8+**；每次改韌體必改 `FW_VERSION`
