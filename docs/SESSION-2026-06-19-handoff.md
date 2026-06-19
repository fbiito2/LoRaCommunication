# 交接 — 2026-06-19（語音 PTT 完成 + 一輪 UX/地圖/pc-gui 修正）

> 回任何機器先 `git pull`。本檔接續 [SESSION-2026-06-15](SESSION-2026-06-15-handoff.md)。

## 一、本 session 完成（皆已 commit+push）

### 語音 PTT（Phase 4）— **程式 code-complete**
- **F-055 Codec2 native lib**：`libcodec2.so` 編出，**arm64-v8a + x86** 兩份都進 APK。編法見 [CODEC2-BUILD.md](CODEC2-BUILD.md)。
- **F-052 韌體 LoRa 模式切換（FW 0.8.0）**：PTT 期間全網切 SF7/BW500、結束回 SF9/BW125；PTT_START 於 SF9 廣播、PTT_END 於 SF7 廣播；35s 逾時保險。
- **APP PTT 管線**：錄音→Codec2 encode→廣播 TYPE_VOICE→收→decode→播放；30s 限時；**對象選擇**(廣播/聯絡人/群組,與文字頁共用)；半雙工自己講不播。
- **FW 0.8.1**：OLED 即時顯示真實 SF(原寫死 7)→ 肉眼可驗模式切換。**四台 CDB8/BF8C/D400/D078 全燒 0.8.1。**

### pc-gui（PC 客戶端）
- 明確連線狀態:未連線(灰)/連線中等回應(橘)/✅已連線(綠)/⚠逾時(紅)。
- 收 SOS 解析並顯示 GPS 座標 + 提示音。

### 地圖 / 節點（F-037）
- 每節點依 DeviceId **固定一色**(混雜雜湊,避開自己青點色帶);清單圓點 + 地圖標記共用。
- 地圖 marker **上方標 ID/暱稱**(自己=「我」);**點自己的青點→卡片切回自己**。
- 節點清單 **排除自己**;修 Razor email 規則把 `0x@n.DeviceIdHex` 印成字面 → 改 `0x@(n.DeviceIdHex)`。

### App 體驗
- **中斷/重連鈕**(文字頁) + **完全結束 App**(設定頁,停前景服務+殺行程→重開全新,解開背景保活後「已連線卻不通」卡死)。

## 二、待續（下次接力）
1. **語音「實際聽得到」未驗** — 程式全好,**只差第二支 Android** 兩機對講。借到任何 Android(min API 24)裝 APK 即可,5 分鐘。
   - (曾想做「手機本機回放自測」零安裝驗 codec+音訊,使用者選先擱著。)
2. **iOS** — 需 Mac(使用者 Mac 過舊、net7 iOS 吃不動)。C# 大致已寫,缺 iOS 音訊(AVAudioEngine)+ `libcodec2.a`。
3. **D400 外接 GPS = 硬體故障**(非韌體)：/version 顯示 baud/rx **一直輪試不鎖定、gps_bytes 趨近 0** → 模組沒送有效 NMEA。查 Grove 接線/供電/模組本身,別查韌體。

## 三、踩雷 / 環境（給「公司新電腦」接力用）
### Codec2 native lib
- **Android(arm64/x86)**：NDK r25c + CMake3.22.1(裝在 `C:\Users\kc\android-ndk-sdk`)+ **VS2019 host vcvars**(VS2022 的 vcvarsall 缺)。**android-26**(複數函式 cabsf 需 API26)。三個 codec2 Windows patch + CRLF batch,全見 CODEC2-BUILD.md。
- **PC client 語音 = 卡關**：Windows 要編 codec2.dll，**MSVC/clang-cl 是死路**——codec2 用大量 C99 複數(`complex float`/`cexpf`…)，MSVC 標頭與 CRT 沒這套(連結失敗)。**官方 Windows 是用 MinGW(gcc)**。要做 PC 雙機語音 → 先裝 **MinGW-w64**(或 LLVM-MinGW),再 codec2.dll + NAudio(NuGet)錄放 + pc-gui 收發。(VLA、M_PI、`_complex` 都可繞，唯複數數學函式要 MinGW 的 libm。)
- **x86 codec2 已備好** 進 APK，但本機 VS2022 模擬器**啟動不了**:古董 emulator 27.2.9 + 開著 Hyper-V → 需 WHPX/AEHD + 更新 emulator(都要系統管理員 + 重開機)。**公司新電腦(模擬器能跑)直接 `git clone` 部署即可測語音**。
- **LLVM 安裝**:winget 機器層/使用者層在本機都失敗(UAC/無使用者層包)。NDK 內其實已附 `clang-cl.exe`(clang14)可用,但對 codec2 仍卡 MSVC 複數(見上)。

### 本機關鍵路徑
- 四台 C6L COM:CDB8=COM4、BF8C=COM5、D400=COM9、D078=COM11(枚舉為準)。手機 S25 adb `R5GL22P3KGV`。
- git remote:`https://github.com/fbiito2/LoRaCommunication.git`。
- APP 部署:`dotnet build app\LoRaPTT\LoRaPTT.csproj -f net7.0-android -c Debug -p:EmbedAssembliesIntoApk=true` → `adb install -r --no-incremental <Signed.apk>`。

## 四、規格完成度（速覽）
**文字/群組/Mesh中繼/SOS+GPS/定位/地圖/HMI/OTA/雙傳輸層/設定 全部完成且實機驗過。**
語音 Android 端 **程式完成、待雙機實測**。未做=選配/後期:iOS、AES加密、離線訊息暫存、長訊息分包、F-035管理式洪泛、Grove感測、Meshtastic橋。
