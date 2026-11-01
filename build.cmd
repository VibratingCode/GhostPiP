@echo off
rem Publishes GhostPiP.exe next to this file. Needs the .NET 10 SDK.
setlocal
cd /d "%~dp0"
dotnet publish src\GhostPiP\GhostPiP.csproj -c Release -p:PublishSingleFile=true -o publish --nologo -v q || exit /b 1
copy /y publish\GhostPiP.exe GhostPiP.exe >nul
rmdir /s /q publish
echo Built %~dp0GhostPiP.exe
