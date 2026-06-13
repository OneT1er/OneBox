@echo off
setlocal

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set OUTDIR=%~dp0output
set SRC=%~dp0

if not exist "%OUTDIR%" mkdir "%OUTDIR%"

echo Compiling OneBox...

"%CSC%" /nologo /target:winexe /out:"%OUTDIR%\OneBox.exe" /codepage:65001 ^
  /win32icon:"%SRC%app.ico" ^
  /reference:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\PresentationCore.dll" ^
  /reference:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\PresentationFramework.dll" ^
  /reference:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\WindowsBase.dll" ^
  /reference:"C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Xaml\v4.0_4.0.0.0__b77a5c561934e089\System.Xaml.dll" ^
  /reference:"System.dll" ^
  /reference:"System.Core.dll" ^
  /reference:"System.Drawing.dll" ^
  /reference:"System.Windows.Forms.dll" ^
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
  "%SRC%MainWindow.cs"

if %ERRORLEVEL% EQU 0 (
    echo Build successful! Output: %OUTDIR%\OneBox.exe
    copy /Y "%SRC%app.ico" "%OUTDIR%\app.ico" >nul
    copy /Y "%SRC%app.png" "%OUTDIR%\app.png" >nul
    copy /Y "%SRC%icon-power.png" "%OUTDIR%\icon-power.png" >nul
    copy /Y "%SRC%icon-audio.png" "%OUTDIR%\icon-audio.png" >nul
) else (
    echo Build failed!
)
endlocal
