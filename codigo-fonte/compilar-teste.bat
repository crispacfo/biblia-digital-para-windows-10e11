@echo off
rem Versao de TESTE (nao distribuir): inclui a captura de tela do autoteste.
rem Uso: BibliaDigital-teste.exe /teste tela  (o exe de teste fica na pasta principal, ao lado da pasta data; apague depois).
rem Recompila o BibliaDigital.exe usando o compilador C# que ja vem com o .NET Framework 4 do Windows.
rem Inclui manifesto (roda sem administrador) e icone, o que ajuda o Windows Defender a nao estranhar o programa.
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /win32manifest:"%~dp0BibliaDigital.manifest" /win32icon:"%~dp0BibliaDigital.ico" /define:AUTOTESTE /out:"%~dp0..\BibliaDigital-teste.exe" /reference:System.Web.Extensions.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "%~dp0BibliaDigital.cs"
pause
