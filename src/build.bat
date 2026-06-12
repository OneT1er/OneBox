@echo off
setlocal

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set OUTDIR=%~dp0output
set SRC=%~dp0

if not exist "%OUTDIR%" mkdir "%OUTDIR%"

echo Compiling PowerAudioManager...

"%CSC%" /nologo /target:winexe /out:"%OUTDIR%\PowerAudioManager.exe" /codepage:65001 ^
  /reference:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\PresentationCore.dll" ^
  /reference:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\PresentationFramework.dll" ^
  /reference:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\WindowsBase.dll" ^
  /reference:"C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Xaml\v4.0_4.0.0.0__b77a5c561934e089\System.Xaml.dll" ^
  /reference:"System.dll" ^
  /reference:"System.Core.dll" ^
  /reference:"System.Drawing.dll" ^
  /reference:"System.Windows.Forms.dll" ^
  "%SRC%App.cs"

if %ERRORLEVEL% EQU 0 (
    echo Build successful! Output: %OUTDIR%\PowerAudioManager.exe
) else (
    echo Build failed!
)
endlocal
