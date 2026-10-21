@echo off
rem Builds a single self-contained ReceiptCapture.exe that runs on any Windows 10/11 PC (no .NET install needed).
dotnet publish ReceiptCapture\ReceiptCapture.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
echo.
echo Done. Your app is in the "publish" folder: publish\ReceiptCapture.exe
pause
