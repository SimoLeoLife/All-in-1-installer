@echo off
REM Publish Windows x64 single-file self-contained binary.
REM Output: publish\win-x64\All-in-1 Installer.exe

setlocal
set CONFIG=Release
set RID=win-x64
set OUT=%~dp0publish\%RID%
set DOTNET_EXE=dotnet

if exist "%~dp0.dotnet\dotnet.exe" set DOTNET_EXE=%~dp0.dotnet\dotnet.exe

if exist "%OUT%" rmdir /s /q "%OUT%"

pushd "%~dp0SourceCode"
"%DOTNET_EXE%" publish "All-in-1 Installer.csproj" ^
    -c %CONFIG% ^
    -r %RID% ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:EnableCompressionInSingleFile=true ^
    -p:DebugType=none ^
    -p:DebugSymbols=false ^
    -o "%OUT%"
set PUBLISH_EXIT=%ERRORLEVEL%
popd

if not "%PUBLISH_EXIT%"=="0" exit /b %PUBLISH_EXIT%

echo.
echo Published Windows build at: %OUT%
echo Run: "%OUT%\All-in-1 Installer.exe"
