@echo off
setlocal
cd /d "%~dp0"
call build.cmd
if errorlevel 1 exit /b 1
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist work mkdir work

"%CSC%" /nologo /utf8output /codepage:65001 /out:work\StartTokenReaderTests.exe /reference:System.dll /reference:System.Core.dll tests\StartTokenReaderTests.cs src\StartTokenReader.cs
if errorlevel 1 exit /b 1
work\StartTokenReaderTests.exe
if errorlevel 1 exit /b 1
"%CSC%" /nologo /utf8output /codepage:65001 /out:work\HBLauncherTokenTests.exe /reference:System.dll /reference:System.Core.dll tests\HBLauncherTokenTests.cs src\StartTokenReader.cs
if errorlevel 1 exit /b 1
work\HBLauncherTokenTests.exe
if errorlevel 1 exit /b 1
start "" /wait dist\TextSender.exe --test-offline "%CD%\work\offline.txt"
if errorlevel 1 exit /b 1
type work\offline.txt

for %%T in (ProjectSwitchUiCheck PresetAutoEnterCheck ScriptSettingsCheck) do (
  "%CSC%" /nologo /target:winexe /utf8output /codepage:65001 /out:work\%%T.exe /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Xml.dll tests\%%T.cs
  if errorlevel 1 exit /b 1
  start "" /wait work\%%T.exe "%CD%\dist\TextSender.exe" "%CD%\work\%%T.txt"
  if errorlevel 1 exit /b 1
  type work\%%T.txt
)
echo All offline checks passed. No hardware or user script was run.
exit /b 0
