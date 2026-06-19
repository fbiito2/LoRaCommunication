# Codec2 native lib 交叉編譯（F-055，libcodec2.so / arm64-v8a）

> 產物：`app/LoRaPTT/libs/codec2/android/arm64-v8a/libcodec2.so`（已 commit）。
> csproj 以 `<AndroidNativeLibrary>` 打進 APK 的 `lib/arm64-v8a/`，`DllImport("codec2")` 載入。
> 平常**不需重編**；只有要更新 Codec2 版本或加 ABI 時才照這份重跑。

## 環境（2026-06-15 本機實證）
- **Android NDK r25c**（`ndk;25.2.9519653`）+ **CMake 3.22.1**（含 Ninja）
  — 用 `cmdline-tools/.../sdkmanager` 裝到使用者目錄 `C:\Users\kc\android-ndk-sdk`（避開 Program Files 要管理員）。
  - sdkmanager 互動式 license 在 .bat 下餵不進去 → **直接寫 `licenses/android-sdk-license`**（標準 hash）headless 接受。
- **Host C 編譯器**：codec2 交叉編譯時會「先用 host 編譯器自編 `generate_codebook` 產生 codebook 原始碼」。
  - ⚠ 本機 **VS2022 Community 的 `vcvarsall.bat` 缺失**（C++ 環境壞）→ 改用 **VS2019 Professional 的 vcvars64.bat**（`vswhere -requires VC.Tools.x86.x64` 指向它）。
- 來源：`git clone --depth 1 https://github.com/drowe67/codec2`（版本 1.2.0）。

## codec2 原始碼需要的 3 個 Windows patch（在 build 目錄改，不入 repo）
1. `src/CMakeLists.txt` ExternalProject `codec2_native`：host 產物在 Windows 是 `.exe`，但 CMake 寫死無副檔名
   → INSTALL_COMMAND / BUILD_BYPRODUCTS / IMPORTED_LOCATION 全加 `.exe`。
2. `src/CMakeLists.txt` 第 84 行：`target_link_libraries(generate_codebook m)` → 用 `if(NOT MSVC)` 包（MSVC 無 `m.lib`）。
3. `CMakeLists.txt` 第 83 行：`set(CMAKE_C_FLAGS ... -Wall -Wno-strict-overflow)` → 用 `if(NOT MSVC)` 包（cl 不認 GCC 旗標，D8021）。

## 關鍵：`ANDROID_PLATFORM=android-26`
- android-21 連結失敗：`cabsf`/`cargf`（C99 複數函式，ofdm.c 用）在 **API 21 的 libm 沒有**，要 **API 26+**。
- 代價：語音需 **Android 8+**；更舊裝置載入 `.so` 失敗 → `Codec2Service` 的 try/catch 接住（`IsAvailable=false`），App 不崩。

## 編譯指令（VS2019 vcvars + NDK toolchain + Ninja）
```bat
call "C:\Program Files (x86)\Microsoft Visual Studio\2019\Professional\VC\Auxiliary\Build\vcvars64.bat"
set PATH=C:\Users\kc\android-ndk-sdk\cmake\3.22.1\bin;%PATH%
cmake -G Ninja ^
  -DCMAKE_TOOLCHAIN_FILE=C:\Users\kc\android-ndk-sdk\ndk\25.2.9519653\build\cmake\android.toolchain.cmake ^
  -DANDROID_ABI=arm64-v8a -DANDROID_PLATFORM=android-26 ^
  -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=ON ..
cmake --build . --target codec2
```
> ⚠ batch 檔必須 **CRLF 換行**（LF 會被 cmd 解析錯亂）。

## 收尾
- `llvm-nm -D` 確認匯出 `codec2_create/destroy/encode/decode`（P/Invoke 需要）→ 有。
- `llvm-strip libcodec2.so`（2.36MB → 1.43MB）後複製到 `app/LoRaPTT/libs/codec2/android/arm64-v8a/`。
- 驗證：APK 內出現 `lib/arm64-v8a/libcodec2.so`（解 APK zip 確認）。

## 其他 ABI
- 已有 **arm64-v8a**(實機) + **x86**(VS 模擬器,`ANDROID_ABI=x86` 同法編,已進 csproj/APK)。
  需 x86_64/armeabi-v7a 改 `ANDROID_ABI` 重編再加進 csproj。

## Windows（PC client 語音）— ⚠ 需 MinGW，MSVC 不行
要讓 pc-gui 也能語音(當第二端點),得編 **codec2.dll(Windows x64)**。實測結論:
- **MSVC / clang-cl 是死路**:codec2 大量用 **C99 複數**(`complex float`、`cexpf/crealf…`)。
  VLA(clang-cl 可解)、`M_PI`(`/D_USE_MATH_DEFINES`)、`_complex`(改 `float _Complex`)都能繞，
  但**複數數學函式 MSVC CRT 根本沒有 → 連結必失敗**。codec2 官方 Windows 就是用 **MinGW**。
- **正解**:裝 **MinGW-w64**(或 LLVM-MinGW),CMake 用 gcc/clang-GNU 編 → `codec2.dll`，
  再 pc-gui P/Invoke + NAudio(NuGet)做 8kHz 錄放 + 收發 TYPE_VOICE。**尚未做。**

## iOS（F-056）
- 需 `libcodec2.a`(Xcode/iOS toolchain,在 Mac 上編),尚未做;另需補 iOS 音訊(AVAudioEngine)。
