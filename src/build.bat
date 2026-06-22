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
  /resource:"%SRC%app.ico",PowerAudioManager.app.ico ^
  /resource:"%SRC%app.png",PowerAudioManager.app.png ^
  /resource:"%SRC%icon-power.png",PowerAudioManager.icon-power.png ^
  /resource:"%SRC%icon-audio.png",PowerAudioManager.icon-audio.png ^
  "%SRC%App.cs" ^
  "%SRC%SettingsDialog.cs" ^
  "%SRC%AppResources.cs" ^
  "%SRC%LauncherBar.cs" ^
  "%SRC%WindowScaling.cs" ^
  "%SRC%TrayController.cs" ^
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
  "%SRC%UpdateChecker.cs" ^
  "%SRC%MainWindow.cs"

if %ERRORLEVEL% EQU 0 (
    echo Build successful! Output: %OUTDIR%\OneBox.exe
    rem All assets (font, icons) are embedded in the exe — no external files needed.
    rem NTFS "file tunneling" can preserve the original CreationTime when the
    rem same filename is rewritten within ~15s. Force-stamp it to "now".
    powershell -NoProfile -Command "$f=Get-Item -LiteralPath '%OUTDIR%\OneBox.exe'; $n=Get-Date; $f.CreationTime=$n; $f.LastWriteTime=$n" >nul 2>&1
) else (
    echo Build failed!
)
endlocal
