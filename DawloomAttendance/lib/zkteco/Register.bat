@echo off
REM ============================================================
REM Dawloom Attendance - ZKTeco SDK Registration
REM Registers the 32-bit zkemkeeper.dll COM ActiveX component.
REM Must be run as Administrator.
REM ============================================================

setlocal
set SCRIPT_DIR=%~dp0
set DLL=%SCRIPT_DIR%zkemkeeper.dll

if not exist "%DLL%" (
    echo [ERROR] zkemkeeper.dll not found in %SCRIPT_DIR%
    echo Download the ZKTeco Standalone SDK and place the DLLs in this folder.
    pause
    exit /b 1
)

echo Registering zkemkeeper.dll (32-bit) ...
"%SystemRoot%\SysWOW64\regsvr32.exe" /s "%DLL%"
if errorlevel 1 (
    echo [ERROR] regsvr32 failed. Re-run this script as Administrator.
    pause
    exit /b 1
)

echo [OK] zkemkeeper.dll registered successfully.
pause
endlocal
