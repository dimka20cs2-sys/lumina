@echo off
rem Build Lumina (by AkeeTech): bin\Lumina.exe (window), bin\Lumina.SelfTest.exe (console self-test), bin\ui\, WebView2 libs
cd /d "%~dp0"
set CSC="%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set REFS=/r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:System.Security.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:Microsoft.VisualBasic.dll /r:System.Core.dll /r:System.Management.dll /r:lib\Microsoft.Web.WebView2.Core.dll /r:lib\Microsoft.Web.WebView2.WinForms.dll
set OPTS=/nologo /codepage:65001 /platform:x64 /optimize+ /warn:3 /nowarn:420 /win32icon:brand\lumina.ico
if not exist bin mkdir bin
for %%f in (Microsoft.Web.WebView2.Core.dll Microsoft.Web.WebView2.WinForms.dll WebView2Loader.dll) do copy /y "lib\%%f" bin\ >nul
%CSC% %OPTS% /target:winexe /main:AkeeLauncher.Program %REFS% /out:bin\Lumina.exe src\*.cs || exit /b 1
%CSC% %OPTS% /target:exe /main:AkeeLauncher.SelfTestProgram %REFS% /out:bin\Lumina.SelfTest.exe src\*.cs || exit /b 1
if exist bin\ui rmdir /s /q bin\ui
xcopy /e /i /q ui bin\ui >nul
copy /y AZURE-SETUP.md bin\ >nul
echo OK: bin\Lumina.exe
