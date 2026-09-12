@echo off
setlocal
set /p "DOWNLOADER_VERSION="<"%~dp0VERSION"
set "DOWNLOADER_EXE=%~dp0downloader-%DOWNLOADER_VERSION%.exe"

if not exist "%DOWNLOADER_EXE%" (
  echo Missing release executable: "%DOWNLOADER_EXE%"
  pause
  exit /b 1
)

set "DOTNET_BUNDLE_EXTRACT_BASE_DIR=%~dp0..\.cache\dotnet-bundle"
start "" "%DOWNLOADER_EXE%"
