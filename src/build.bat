@echo off
setlocal

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set OUTDIR=%~dp0output
set SRC=%~dp0

if not exist "%OUTDIR%" mkdir "%OUTDIR%"

echo Compiling OneBox...

rem Force a fresh CreationTime: NTFS file tunneling otherwise keeps the original
rem timestamp on overwrite, which makes the Explorer "Date created" column look
rem stale even though /out: just rewrote the file. Deleting first sidesteps it.
if exist "%OUTDIR%\OneBox.exe" del /f /q "%OUTDIR%\OneBox.exe"

"%CSC%" /nologo /target:winexe /out:"%OUTDIR%\OneBox.exe" /codepage:65001 ^
  /win32icon:"%SRC%app.ico" ^
  /win32manifest:"%SRC%app.manifest" ^
  /reference:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\PresentationCore.dll" ^
  /reference:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\PresentationFramework.dll" ^
  /reference:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\WindowsBase.dll" ^
  /reference:"C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Xaml\v4.0_4.0.0.0__b77a5c561934e089\System.Xaml.dll" ^
  /reference:"System.dll" ^
  /reference:"System.Core.dll" ^
  /reference:"System.Drawing.dll" ^
  /reference:"System.Windows.Forms.dll" ^
  /reference:"System.Security.dll" ^
  /reference:"System.Web.Extensions.dll" ^
  "%SRC%App.cs" ^
  "%SRC%Native.cs" ^
  "%SRC%Models.cs" ^
  "%SRC%Prefs.cs" ^
  "%SRC%PowerPlanService.cs" ^
  "%SRC%AudioDevices.cs" ^
  "%SRC%VolumeControl.cs" ^
  "%SRC%MemoryCleaner.cs" ^
  "%SRC%TranslateService.cs" ^
  "%SRC%AdminUtils.cs" ^
  "%SRC%Dialogs.cs" ^
  "%SRC%ClipboardHistory.cs" ^
  "%SRC%MainWindow.cs"

if %ERRORLEVEL% EQU 0 (
    echo Build successful! Output: %OUTDIR%\OneBox.exe
    copy /Y "%SRC%app.ico" "%OUTDIR%\app.ico" >nul
    copy /Y "%SRC%app.png" "%OUTDIR%\app.png" >nul
    copy /Y "%SRC%icon-power.png" "%OUTDIR%\icon-power.png" >nul
    copy /Y "%SRC%icon-audio.png" "%OUTDIR%\icon-audio.png" >nul
    rem Ship app.config as OneBox.exe.config so the .NET runtime reads the per-monitor
    rem DPI AppContextSwitchOverrides (the config must sit next to the exe, named <exe>.config).
    copy /Y "%SRC%app.config" "%OUTDIR%\OneBox.exe.config" >nul
    rem Ship the HarmonyOS Sans SC font next to the exe so the app is portable and
    rem does not depend on a hardcoded developer-machine path. Best-effort: if the
    rem source ttf is not present (e.g. on another machine), skip silently and the
    rem app falls back to Microsoft YaHei UI at runtime. (Path inlined because
    rem %VAR% inside a parenthesized block expands at parse time, before any set.)
    if exist "C:\Users\LIUxy\OneDrive\Documents\tools\美化与字体\HarmonyOS-Sans\HarmonyOS Sans\HarmonyOS_Sans_SC\HarmonyOS_Sans_SC_Regular.ttf" copy /Y "C:\Users\LIUxy\OneDrive\Documents\tools\美化与字体\HarmonyOS-Sans\HarmonyOS Sans\HarmonyOS_Sans_SC\HarmonyOS_Sans_SC_Regular.ttf" "%OUTDIR%\HarmonyOS_Sans_SC_Regular.ttf" >nul
    rem NTFS "file tunneling" can preserve the original CreationTime when the
    rem same filename is rewritten within ~15s. Force-stamp it to "now".
    powershell -NoProfile -Command "$f=Get-Item -LiteralPath '%OUTDIR%\OneBox.exe'; $n=Get-Date; $f.CreationTime=$n; $f.LastWriteTime=$n" >nul 2>&1
) else (
    echo Build failed!
)
endlocal
