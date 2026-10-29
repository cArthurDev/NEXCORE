@echo off
setlocal
rem Gera o NEXCORE pronto para levar a outro PC (nao precisa instalar o .NET la).
rem Saida: ..\NEXCORE-dist\NEXCORE.exe + LEIA-ME.txt
cd /d "%~dp0"
set "DOTNET=%~dp0.tools\dotnet\dotnet.exe"
if exist "%DOTNET%" goto sdk_check
set "DOTNET=dotnet"

:sdk_check
"%DOTNET%" --list-sdks 2>nul | findstr /r /c:"^8\." >nul
if errorlevel 1 goto missing_sdk

set "OUTPUT=%~dp0..\NEXCORE-dist"
echo Gerando NEXCORE.exe a partir do codigo atual ...
echo Se a versao portatil estiver aberta, feche-a antes de continuar.
"%DOTNET%" publish "%~dp0NEXCORE.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o "%OUTPUT%" -nologo -v minimal
if errorlevel 1 goto failed
if not exist "%OUTPUT%\NEXCORE.exe" goto failed
copy /y "%~dp0LEIA-ME.txt" "%OUTPUT%\LEIA-ME.txt" >nul
if errorlevel 1 goto failed
copy /y "%~dp0LICENSE" "%OUTPUT%\LICENSE" >nul
if errorlevel 1 goto failed
echo.
echo Pronto: %OUTPUT%\NEXCORE.exe
for %%F in ("%OUTPUT%\NEXCORE.exe") do echo Arquivo gerado em %%~tF - %%~zF bytes
if /i not "%~1"=="--no-pause" pause
exit /b 0

:missing_sdk
echo.
echo Nao foi encontrado um SDK .NET 8 no sistema nem em .tools\dotnet.
echo Instale o SDK .NET 8 ou restaure o SDK local do projeto.
goto failed

:failed
echo.
echo FALHOU. Veja os erros acima. A publicacao nao foi concluida.
if /i not "%~1"=="--no-pause" pause
exit /b 1
