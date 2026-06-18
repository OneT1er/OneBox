@echo off
chcp 65001 >nul
cd /d "%~dp0"

set PROXY=http://127.0.0.1:7897

echo [1/4] Staging all changes...
git add -A

echo.
echo Files staged:
git status --short

echo.
echo [2/4] Committing...
git commit -F "%~dp0\commit_v101.txt"

echo.
echo [3/4] Setting proxy...
git config --global http.proxy %PROXY%
git config --global https.proxy %PROXY%

echo.
echo [4/4] Pushing to GitHub...
git push

echo.
if %ERRORLEVEL% EQU 0 (
    echo ============================================
    echo SUCCESS. v1.0.1 changes pushed.
    echo ============================================
) else (
    echo FAILED - check proxy / auth
)
pause
