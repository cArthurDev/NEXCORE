@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -File "%~dp0installer\Build-Installer.ps1"
if errorlevel 1 goto failed
echo.
echo Instalador gerado na pasta installer\output.
if /i not "%~1"=="--no-pause" pause
exit /b 0
:failed
echo.
echo FALHOU. Veja os erros acima. O instalador nao foi atualizado.
if /i not "%~1"=="--no-pause" pause
exit /b 1
