@echo off
rem Builds Aegis Docs into the "publish" folder. It runs on any Windows 10/11 PC (no .NET install needed).
dotnet publish ReceiptCapture\ReceiptCapture.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish

rem Meeting transcripts need the speech engine files and the speech model.
powershell -NoProfile -ExecutionPolicy Bypass -File tools\add-speech-engine.ps1 -PublishDir publish
if errorlevel 1 (
  echo The speech engine files could not be added. Transcripts will not work.
  pause
  exit /b 1
)

rem The speech model is about 180 MB. Download it once into publish\models.
if not exist publish\models\ggml-small.en-q5_1.bin (
  mkdir publish\models 2>nul
  curl -L -o publish\models\ggml-small.en-q5_1.bin https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en-q5_1.bin
)

echo.
echo Done. Run publish\AegisDocs.exe. Keep the whole "publish" folder together:
echo the "runtimes" and "models" folders next to the exe are needed for meeting transcripts.
pause
