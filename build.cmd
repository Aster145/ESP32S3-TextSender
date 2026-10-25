@echo off
setlocal EnableExtensions
set "PROJECT_ROOT=%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo ERROR: .NET Framework C# compiler was not found. Install .NET Framework 4.8.
    exit /b 1
)

if not exist "%PROJECT_ROOT%dist" (
    mkdir "%PROJECT_ROOT%dist"
    if errorlevel 1 exit /b 1
)

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /utf8output /codepage:65001 /out:"%PROJECT_ROOT%dist\TextSender.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Xml.dll "%PROJECT_ROOT%src\TextSender.cs" "%PROJECT_ROOT%src\StartTokenReader.cs" "%PROJECT_ROOT%src\ExplorerFolderPicker.cs"
if errorlevel 1 exit /b 1

if not exist "%PROJECT_ROOT%dist\settings.xml" (
    if not exist "%PROJECT_ROOT%settings.example.xml" (
        echo ERROR: settings.example.xml is missing.
        exit /b 1
    )
    copy /y "%PROJECT_ROOT%settings.example.xml" "%PROJECT_ROOT%dist\settings.xml" >nul
    if errorlevel 1 exit /b 1
)
copy /y "%PROJECT_ROOT%README.md" "%PROJECT_ROOT%dist\README.md" >nul
if errorlevel 1 exit /b 1

echo Built: "%PROJECT_ROOT%dist\TextSender.exe"
echo Settings: "%PROJECT_ROOT%dist\settings.xml"
exit /b 0
