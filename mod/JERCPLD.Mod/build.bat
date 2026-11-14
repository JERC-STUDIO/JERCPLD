@echo off
setlocal EnableDelayedExpansion
if not defined LEVILAMINA_SRC set "LEVILAMINA_SRC=D:\ylcs\_tools\ll-src\LeviLamina-26.51.5\src"
if not defined LEVILAMINA_LIB_DIR set "LEVILAMINA_LIB_DIR=D:\ylcs\_tools"

if not exist "%LEVILAMINA_SRC%\ll\api\mod\NativeMod.h" (
  echo [JERCPLD] LeviLamina headers not found: %LEVILAMINA_SRC%
  echo [JERCPLD] set LEVILAMINA_SRC to the LeviLamina src directory
  exit /b 1
)
if not exist "%LEVILAMINA_LIB_DIR%\LeviLamina.lib" (
  echo [JERCPLD] LeviLamina.lib not found: %LEVILAMINA_LIB_DIR%
  echo [JERCPLD] set LEVILAMINA_LIB_DIR to the directory containing LeviLamina.lib
  exit /b 1
)

set "VCVARS="
if exist "D:\VS\BuildTools\VC\Auxiliary\Build\vcvars64.bat" set "VCVARS=D:\VS\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
if not defined VCVARS (
  for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSPATH=%%i"
  if defined VSPATH if exist "!VSPATH!\VC\Auxiliary\Build\vcvars64.bat" set "VCVARS=!VSPATH!\VC\Auxiliary\Build\vcvars64.bat"
)
if not defined VCVARS (
  echo [JERCPLD] vcvars64.bat not found
  exit /b 1
)
call "%VCVARS%" >nul
if errorlevel 1 exit /b 1

if not exist "%~dp0build" mkdir "%~dp0build"
cl /nologo /std:c++20 /EHa /MD /LD /utf-8 /W4 ^
  /DNDEBUG /D_ITERATOR_DEBUG_LEVEL=0 /D_HAS_CXX23=1 ^
  /DNOMINMAX /DUNICODE /D_UNICODE /DDEBUG_ASSERT_DISABLE /DFMT_HEADER_ONLY ^
  /DENTT_PACKED_PAGE=128 /DENTT_SPARSE_PAGE=4096 /DENTT_NO_MIXIN /DLL_PLAT_S ^
  /I"%~dp0src" /I"%LEVILAMINA_SRC%" ^
  /I"%~dp0deps\concurrentqueue-master" /I"%~dp0deps\parallel-hashmap-master" ^
  /I"%~dp0deps\entt" /I"%~dp0deps\fmt-11.2.0\include" /I"%~dp0deps\nlohmann" ^
  /I"%~dp0deps\glm-1.0.1" /I"%~dp0deps\gsl-4.2.0\include" ^
  /I"%~dp0deps\leveldb-1.23\include" /I"%~dp0deps\rapidjson-master\include" ^
  /I"%~dp0deps\type_safe-0.2.4\include" /I"%~dp0deps\expected-lite-0.8.0\include" ^
  /I"%~dp0deps\magic_enum-0.9.7\include" /I"%~dp0deps\pcg-cpp-master\include" ^
  /I"%~dp0deps\ctre-3.8.1\include" /I"%~dp0deps\stb" ^
  "%~dp0src\JerCpldMod.cpp" ^
  /Fe:"%~dp0build\JERCPLD.dll" ^
  /link /LIBPATH:"%LEVILAMINA_LIB_DIR%" LeviLamina.lib
if errorlevel 1 exit /b 1
copy /Y "%~dp0manifest.json" "%~dp0build\manifest.json" >nul
copy /Y "%~dp0tooth.json" "%~dp0build\tooth.json" >nul

REM ---- PTL 规则模块已迁到独立仓库 JERCPTL（github.com/JERC-STUDIO/JERCPTL）----
REM 本仓只构建引擎；安装时用 install.ps1 -PtlDll <JERCPTL 的 build\JERCPLD.PTL.dll> 带上模块。
echo BUILD OK
