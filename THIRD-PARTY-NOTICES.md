# Third-party notices

Aegis Docs itself is proprietary software, Copyright (c) 2026 Aegis Labs (see `LICENSE.txt`). The components below are licensed to you under their own terms.

| Component | Use | Licence |
|---|---|---|
| Sora typeface (Sora Project Authors, https://github.com/sora-xor/sora-font) | UI font, embedded from `ReceiptCapture/Fonts` | SIL Open Font License 1.1, see `ReceiptCapture/Fonts/OFL.txt`. The font files here are static instances cut from the original variable font and renamed for WPF; the licence allows this, and they may not be sold on their own. |
| ModernWpfUI (https://github.com/Kinnara/ModernWpf) | Modern control styles and light/dark templates | MIT |
| PDFsharp (https://github.com/empira/PDFsharp) | Merge, split and stamp PDFs | MIT |
| NAudio (https://github.com/naudio/NAudio) | Records the microphone and computer sound for the meeting recorder | MIT |
| Whisper.net (https://github.com/sandrohanea/whisper.net) | Runs the speech-to-text model for meeting transcripts, fully offline | MIT |
| whisper.cpp / ggml (https://github.com/ggml-org/whisper.cpp) | The speech engine inside Whisper.net, and the converted model file `ggml-small.en-q5_1.bin` | MIT |
| OpenAI Whisper model weights (https://github.com/openai/whisper) | The speech-to-text model itself (small.en, compressed) | MIT |
| System.Security.Cryptography.ProtectedData (https://github.com/dotnet/runtime) | Keeps the key that locks the saved data tied to the Windows sign-in | MIT |
| Microsoft Visual C++ runtime DLLs | Needed by the speech engine; copied beside it so the app works on a PC that hasn't installed them | Redistributable under the Visual Studio licence terms |

Please re-check each licence text before distributing the app commercially.
