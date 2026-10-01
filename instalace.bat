@echo off
setlocal
cd /d "%~dp0"
echo.
echo  ==== Reversal ^& Confirmation Entry - instalace do ATAS ====
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
  echo  Chybi .NET 10 SDK.
  echo  Stahni a nainstaluj: https://dotnet.microsoft.com/download/dotnet/10.0
  echo  Potom tento soubor spust znovu.
  echo.
  pause
  exit /b 1
)

set "ATAS_BASE=C:\Program Files (x86)\ATAS Platform"
if not exist "%ATAS_BASE%\ATAS.Indicators.dll" set "ATAS_BASE=C:\Program Files\ATAS Platform"
if not exist "%ATAS_BASE%\ATAS.Indicators.dll" (
  echo  ATAS jsem nenasel ve vychozi slozce.
  set /p "ATAS_BASE=Zadej slozku, kde je soubor ATAS.Indicators.dll: "
)
if not exist "%ATAS_BASE%\ATAS.Indicators.dll" (
  echo  V zadane slozce neni ATAS.Indicators.dll.
  echo.
  pause
  exit /b 1
)

echo  1/3 Sestavuji indikator proti ATAS v "%ATAS_BASE%" ...
dotnet build src\Atas\ReversalConfirmation.Atas.csproj -c Release -nologo -v quiet "-p:ATAS_BASE=%ATAS_BASE%"
if errorlevel 1 (
  echo.
  echo  Sestaveni selhalo. Zkopiruj text chyby vyse a posli ho.
  echo.
  pause
  exit /b 1
)

echo  2/3 Kopiruji indikator do %APPDATA%\ATAS\Indicators ...
if not exist "%APPDATA%\ATAS\Indicators" mkdir "%APPDATA%\ATAS\Indicators"
copy /Y "src\Atas\bin\Release\net10.0-windows\ReversalConfirmation.dll" "%APPDATA%\ATAS\Indicators\" >nul
if errorlevel 1 (
  echo.
  echo  Kopirovani selhalo. Zavri ATAS a spust tento soubor znovu.
  echo.
  pause
  exit /b 1
)

echo  3/3 Kopiruji kalibraci (namerene pravdepodobnosti) ...
if not exist "%APPDATA%\ATAS\ReversalConfirmation" mkdir "%APPDATA%\ATAS\ReversalConfirmation"
copy /Y "calibration\es_m5_2024-07_2026-06.json" "%APPDATA%\ATAS\ReversalConfirmation\calibration.json" >nul

echo.
echo  Hotovo. Spust (restartuj) ATAS a na graf ES M5 pridej indikator
echo  "Reversal & Confirmation Entry". Navod: docs\Brozurka.html
echo.
pause
