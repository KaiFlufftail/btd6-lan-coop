@echo off
rem Opens the relay's port to the local network only. Windows blocks inbound
rem connections by default, which looks exactly like a join that never arrives. Scoped by
rem remote address rather than by firewall profile, because Windows often labels a home
rem Wi-Fi network Public and a private-profile rule then does nothing. Asks for
rem administrator rights because firewall rules need them. Run once per machine.
set PORT=%1
if "%PORT%"=="" set PORT=1445

net session >nul 2>&1
if errorlevel 1 (
    echo Asking for administrator rights...
    powershell -c "Start-Process -Verb RunAs -FilePath '%~f0' -ArgumentList '%PORT%'"
    exit /b
)

netsh advfirewall firewall delete rule name="BTD6 LAN relay" >nul 2>&1
netsh advfirewall firewall add rule name="BTD6 LAN relay" dir=in action=allow protocol=TCP localport=%PORT% remoteip=LocalSubnet
echo.
echo Port %PORT% is now open to the local network.
echo Remove it later with: netsh advfirewall firewall delete rule name="BTD6 LAN relay"
pause
