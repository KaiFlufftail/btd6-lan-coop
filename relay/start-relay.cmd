@echo off
rem Double-click to host a LAN co-op relay. Pass a port number to use something other
rem than 1445. The window stays open after the relay stops so the log can be read.
title BTD6 LAN relay
"%~dp0btd6relay.exe" %*
echo.
echo Relay stopped. Press any key to close.
pause >nul
