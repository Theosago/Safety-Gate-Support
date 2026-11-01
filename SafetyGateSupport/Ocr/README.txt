Offline drawing orientation resources

Keep this Ocr folder with SafetyGateSupport.exe. No OCR installation, account,
or runtime download is required. Drawing images are not uploaded anywhere.

The executable and image libraries are copied at build time from the pinned
TesseractOCR NuGet package 5.5.2. The three app-local Microsoft C++ runtime DLLs
come from NtvLibs.MSVCP packages 14.42.34430; CI verifies Microsoft signatures.
Package licenses and notices are included in ThirdPartyNotices.

English language data: tesseract-ocr/tessdata_fast, commit
87416418657359cb625c412a48b6e1d6d41c29bd, eng.traineddata (Apache 2.0).
SHA-256: 7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2

The app first uses embedded PDF text baselines. Image-only drawings are checked
in both landscape reading directions with offline OCR. Orientation results are
cached locally by drawing content and algorithm version. Blank/unreadable pages
fall back to landscape layout; no algorithm can infer an absent reading direction.
