@echo off
rem Compile-only check used by CI (no link, no LeviLamina.lib required).
setlocal EnableDelayedExpansion
if not defined LEVILAMINA_SRC set "LEVILAMINA_SRC=D:\ylcs\_tools\ll-src\LeviLamina-26.51.5\src"

if not exist "%LEVILAMINA_SRC%\ll\api\mod\NativeMod.h" (
  echo [JERCPLD] LeviLamina headers not found: %LEVILAMINA_SRC%
  exit /b 1
)
if not exist "%~dp0deps\entt\entt\entt.hpp" (
  echo [JERCPLD] deps missing, run fetch-deps.ps1 first
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
cl /nologo /std:c++20 /EHa /MD /utf-8 /W1 /c ^
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
  "%~dp0src\JerCpldMod.cpp" /Fo:"%~dp0build\JerCpldMod.obj"
if errorlevel 1 exit /b 1
echo COMPILE OK
