@echo off
REM Needs the .NET 8 SDK on THIS (build) PC only. The output runs anywhere with no install.
dotnet publish -c Release -o dist
echo.
echo Done. Copy dist\LaptopCheck.exe to your USB stick.
pause
