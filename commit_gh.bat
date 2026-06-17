@echo off
chcp 65001 >nul
cd /d "%~dp0"

echo Staging changed files...
git add .gitignore README.md src/MainWindow.cs src/build.bat src/UpdateChecker.cs src/HarmonyOS_Sans_SC_Regular.ttf

echo Committing...
git commit -F "%~dp0commit_gh.txt"

echo.
echo === Next: push to GitHub ===
echo 1. Create an empty repo on github.com (do NOT add README/license there)
echo 2. Run these once (replace YOUR_USERNAME):
echo      git remote add origin https://github.com/YOUR_USERNAME/OneBox.git
echo      git branch -M main
echo      git push -u origin main
echo.
echo Done. You can delete commit_gh.bat and commit_gh.txt afterwards.
pause
