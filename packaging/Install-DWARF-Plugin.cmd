@echo off
setlocal
set "SRC=%~dp0DWARF"
set "DEST=%LOCALAPPDATA%\NINA\Plugins\3.0.0\DWARF"
echo DWARF plugin installer for N.I.N.A. 3.x
echo.
tasklist /FI "IMAGENAME eq NINA.exe" 2>nul | find /I "NINA.exe" >nul
if not errorlevel 1 (
  echo N.I.N.A. is running. Please close N.I.N.A. first, then run this file again.
  pause
  exit /b 1
)
if not exist "%SRC%\DWARF.NINA.Plugin.dll" (
  echo Could not find the DWARF folder next to this file.
  echo Extract the whole zip first, then run this file from the extracted folder.
  pause
  exit /b 1
)
if not exist "%DEST%" mkdir "%DEST%"
copy /Y "%SRC%\*" "%DEST%\" >nul
if errorlevel 1 (
  echo Copy failed.
  pause
  exit /b 1
)
echo Installed to:
echo   %DEST%
echo.
echo Start N.I.N.A. and check Options - Plugins - Installed for "DWARF".
explorer "%DEST%"
pause
