@echo off
rem Starts Spread. Spread has no .exe of its own; it runs as "dotnet Spread.dll".
rem Any arguments are passed on, for example:  Spread.cmd --toggle
setlocal
set "DLL=%~dp0Spread.dll"
if not exist "%DLL%" (
  echo Spread.dll was not found next to this file. Extract the whole zip first.
  pause
  exit /b 1
)
where dotnet >nul 2>nul
if errorlevel 1 goto missing
dotnet --list-runtimes 2>nul | findstr /b /c:"Microsoft.WindowsDesktop.App 8." >nul
if errorlevel 1 goto missing

rem Minimized: dotnet.exe is a console program, so a console window exists while Spread runs. Closing it ends Spread.
start "" /min dotnet "%DLL%" %*
exit /b 0

:missing
echo Spread needs the .NET 8 Desktop Runtime, which is not installed.
echo Opening the download page. Install ".NET Desktop Runtime 8 (x64)", then start Spread again.
start "" "https://dotnet.microsoft.com/download/dotnet/8.0/runtime"
pause
exit /b 1
