@echo off
REM ===========================================================================
REM  TNX Options Profile - build and install for AMP Quantower
REM ===========================================================================

setlocal
cd /d "%~dp0"

set "AMP_BIN=C:\AMP Quantower\TradingPlatform\v1.146.7\bin"
set "AMP_IND=C:\AMP Quantower\Settings\Scripts\Indicators"

echo Building for AMP Quantower (v1.146.7) ...
dotnet build -v minimal -p:QuantowerBin="%AMP_BIN%"
if errorlevel 1 goto :fail

if exist "%AMP_IND%" (
    copy /Y "bin\TNXOptionsProfile.dll" "%AMP_IND%\TNXOptionsProfile.dll" >nul
    echo   OK  Copied to %AMP_IND%
) else (
    echo   SKIP Indicators folder not found
)

echo Done. Restart Quantower to load the new build.
goto :eof

:fail
echo BUILD FAILED - nothing was installed.
exit /b 1