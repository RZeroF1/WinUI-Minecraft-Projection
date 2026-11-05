@echo off
setlocal
set "ARCH=%~1"
if "%ARCH%"=="" set "ARCH=x64"
set "OUT=%~2"
if "%OUT%"=="" set "OUT=%~dp0..\..\artifacts\native\win-%ARCH%"
set "WINUI_INCLUDE=%~3"
if not defined WINUI_INCLUDE set "WINUI_INCLUDE=%LOCALAPPDATA%\NuGet\microsoft.windowsappsdk.winui\2.3.9\include"
if not exist "%OUT%" mkdir "%OUT%"
for %%I in ("%OUT%") do set "OUT=%%~fI"
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
for /f "usebackq tokens=*" %%I in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSROOT=%%I"
if not defined VSROOT (
  echo Visual Studio C++ build tools are required to build the projection renderer.
  exit /b 1
)
call "%VSROOT%\Common7\Tools\VsDevCmd.bat" -no_logo -arch=%ARCH% -host_arch=x64
if errorlevel 1 exit /b %errorlevel%
if not exist "%WINUI_INCLUDE%\microsoft.ui.xaml.media.dxinterop.h" (
  echo Restore Microsoft.WindowsAppSDK or pass its WinUI include directory as the third argument.
  exit /b 1
)
pushd "%OUT%"
cl /nologo /O2 /MT /LD /Brepro /EHsc /std:c++17 /I"%WINUI_INCLUDE%" /I"%~dp0." "%~dp0renderer.cpp" /link /Brepro d3d11.lib dxgi.lib d3dcompiler.lib windowscodecs.lib ole32.lib /OUT:rzero_projection_d3d11.dll
set "RESULT=%errorlevel%"
popd
exit /b %RESULT%
