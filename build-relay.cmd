@echo off
rem Builds a standalone Windows relay into dist\win-x64. Needs the .NET SDK; players who
rem only want to run it should use the binary from someone who has built it.
dotnet publish "%~dp0relay\relay.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=true -p:DebugType=none -o "%~dp0dist\win-x64"
copy /Y "%~dp0relay\start-relay.cmd" "%~dp0dist\win-x64\"
copy /Y "%~dp0relay\allow-firewall.cmd" "%~dp0dist\win-x64\"
echo.
echo dist\win-x64\btd6relay.exe ready, or double-click start-relay.cmd
pause
