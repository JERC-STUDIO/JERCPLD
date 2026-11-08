@echo off
setlocal
call "D:\VS\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul
cl /nologo /std:c++20 /EHa /MD /utf-8 /W1 /showIncludes ^
  /DNDEBUG /D_ITERATOR_DEBUG_LEVEL=0 /D_HAS_CXX23=1 ^
  /DNOMINMAX /DUNICODE /D_UNICODE /DLL ^
  /DENTT_PACKED_PAGE=128 /DENTT_SPARSE_PAGE=4096 /DENTT_NO_MIXIN /DLL_PLAT_S ^
  /I"%~dp0src" /I"D:\ylcs\_tools\ll-src\LeviLamina-26.51.5\src" ^
  /I"%~dp0deps\concurrentqueue-master" /I"%~dp0deps\parallel-hashmap-master" ^
  /I"%~dp0deps\entt" /I"%~dp0deps\fmt-main\include" /I"%~dp0deps\nlohmann" ^
  /c "%~dp0src\JerCpldMod.cpp" /Fo:"%~dp0build\JerCpldMod.obj" > "%~dp0build\includes.log" 2>&1
