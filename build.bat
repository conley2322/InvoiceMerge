@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion
set ROOT=%~dp0
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set SRC=%ROOT%src\InvoiceMerge.cs
set LIB=%ROOT%lib
set OUT=%ROOT%release\InvoiceMerge.exe

echo ================================================
echo   InvoiceMerge  BUILD  (single-file exe)
echo ================================================
echo.

if not exist "%CSC%" (
  echo [ERROR] csc.exe not found: %CSC%
  goto :end
)
if not exist "%SRC%" (
  echo [ERROR] source not found: %SRC%
  goto :end
)
if not exist "%LIB%\itextsharp.dll" (
  echo [ERROR] missing %LIB%\itextsharp.dll
  echo         NuGet: iTextSharp 5.5.13.3
  goto :end
)
if not exist "%LIB%\BouncyCastle.Cryptography.dll" (
  echo [ERROR] missing %LIB%\BouncyCastle.Cryptography.dll
  echo         NuGet: BouncyCastle.Cryptography 2.x
  goto :end
)

if not exist "%ROOT%release" md "%ROOT%release"
if exist "%OUT%" del /q "%OUT%"

rem 源码 UTF-8 无 BOM 时 csc 会按 GBK 读，导致中文判断失效；补 BOM
powershell -NoProfile -ExecutionPolicy Bypass -Command "$p='%SRC%'; $t=[IO.File]::ReadAllText($p,[Text.Encoding]::UTF8); [IO.File]::WriteAllText($p,$t,(New-Object Text.UTF8Encoding($true)))"

rem 应用图标（assets\app.ico 存在才嵌入，可选）
set ICO=
if exist "%ROOT%assets\app.ico" set ICO=-win32icon:"%ROOT%assets\app.ico"

"%CSC%" -nologo -target:winexe -platform:anycpu -optimize+ ^
  -out:"%OUT%" ^
  -r:"%LIB%\itextsharp.dll" ^
  -r:"%LIB%\BouncyCastle.Cryptography.dll" ^
  -r:System.Windows.Forms.dll ^
  -r:System.Drawing.dll ^
  -resource:"%LIB%\itextsharp.dll" ^
  -resource:"%LIB%\BouncyCastle.Cryptography.dll" ^
  %ICO% ^
  "%SRC%"

echo.
if not exist "%OUT%" (
  echo BUILD FAILED - check the errors above
  goto :end
)
echo BUILD OK: %OUT%
echo.
echo --- version stamp ---
"%OUT%" --version
ping -n 2 127.0.0.1 >nul

rem ===== 单文件自测：exe 单独放到空目录，不带任何 dll =====
set TESTDIR=%TEMP%\im_singlefile_test
if exist "%TESTDIR%" rd /s /q "%TESTDIR%"
md "%TESTDIR%" 2>nul
copy /y "%OUT%" "%TESTDIR%\InvoiceMerge.exe" >nul
echo --- single-file test: exe alone in an empty folder ---
"%TESTDIR%\InvoiceMerge.exe" --where > "%TESTDIR%\where.txt" 2>&1
type "%TESTDIR%\where.txt"
ping -n 2 127.0.0.1 >nul
findstr /c:"iTextSharp" "%TESTDIR%\where.txt" >nul 2>&1
if not errorlevel 1 (
  echo [PASS] 依赖已嵌入 exe
) else (
  echo [FAIL] 单独运行时无法加载 iTextSharp，嵌入可能未生效
)
echo ------------------------------------------------
dir "%OUT%" | findstr /i "InvoiceMerge.exe"

:end
echo.
pause
