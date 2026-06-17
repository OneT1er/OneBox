@echo off
chcp 65001 >nul
cd /d "%~dp0"

echo Staging ALL changes (new + modified)...
git add -A

echo.
echo === Files about to be committed ===
git status --short

echo.
echo Committing...
git commit -F "%~dp0commit_save.txt"

echo.
echo Done. Current progress is saved. You can delete commit_save.bat and commit_save.txt afterwards.
pause
