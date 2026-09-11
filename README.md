# SKYVIDEO EDITOR

完全離線的 Windows 桌面影片剪輯與合成工具。影片只在本機處理，不需要瀏覽器、localhost 或網路服務。

## 功能

- 一次加入多支影片，也可直接拖曳到程式視窗。
- 支援 MP4、MOV、MKV、WebM、AVI、M4V、WMV、FLV、MPEG、MPG、TS、MTS、M2TS。
- 逐筆預覽、拖曳排序、移除片段、設定入點／出點。
- 「分別輸出」產生多個 MP4；「合併輸出」依清單順序產生一個 MP4。
- 批次讀取、剪輯與合成會顯示完成進度。

## 從原始碼建置

需要 Windows 內建的 .NET Framework C# 編譯器（通常隨 Windows 提供），不需要 NuGet。

```powershell
.\build_native.ps1
```

輸出檔案為 `dist\SKYVIDEO_EDITOR.exe`。也可以雙擊 `build_native.bat` 建置。

## FFmpeg

原始碼包不包含大型第三方 `ffmpeg.exe`。請從 [FFmpeg 官方網站](https://ffmpeg.org/download.html) 取得 Windows 版本，放到 EXE 同一資料夾，或在程式內按「FFmpeg 設定」選擇它。FFmpeg 依其自身授權條款發佈。

## 直接使用 Release

請下載 Release 的 `SKYVIDEO_EDITOR-Portable-v1.7.zip`，完整解壓縮後執行 `SKYVIDEO_EDITOR.exe`；ZIP 內已附上 FFmpeg。

## 授權

本專案採 MIT License。FFmpeg 是獨立的第三方元件。
