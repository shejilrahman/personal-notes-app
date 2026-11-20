@echo off
rem Builds bin\PersonalNotes.exe with the C# compiler that ships with Windows.
rem The interface files (src\*.xaml) are embedded inside the exe.
setlocal
set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\csc.exe" set "FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"

rem The exe cannot be replaced while it is running.
taskkill /im PersonalNotes.exe /f >nul 2>&1 && ping -n 2 127.0.0.1 >nul

if not exist "%~dp0bin" mkdir "%~dp0bin"
"%FW%\csc.exe" /nologo /target:winexe /optimize+ /out:"%~dp0bin\PersonalNotes.exe" ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /r:Microsoft.VisualBasic.dll /r:System.Xaml.dll ^
  /r:"%FW%\WPF\WindowsBase.dll" /r:"%FW%\WPF\PresentationCore.dll" /r:"%FW%\WPF\PresentationFramework.dll" ^
  /r:"%FW%\WPF\System.Speech.dll" ^
  /resource:"%~dp0src\Styles.xaml",Styles.xaml ^
  /resource:"%~dp0src\MainView.xaml",MainView.xaml ^
  /resource:"%~dp0src\PasteBar.xaml",PasteBar.xaml ^
  "%~dp0src\PersonalNotes.cs"
if errorlevel 1 exit /b 1

echo Built "%~dp0bin\PersonalNotes.exe"
