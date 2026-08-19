@echo off
REM 启动 POA 证据库(.NET 版)。默认复用同级 amazon-poa-evidence 的 data/uploads/public。
setlocal
set POA_ROOT=%~dp0..\amazon-poa-evidence
if not exist "%POA_ROOT%" set POA_ROOT=%~dp0data
set PORT=3000
if not "%1"=="" set PORT=%1
echo POA_ROOT=%POA_ROOT%  PORT=%PORT%
"%~dp0poa-net.exe"
endlocal
