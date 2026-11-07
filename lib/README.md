# lib

빌드에 필요한 외부 어셈블리를 저장소에 함께 둡니다. self-hosted 러너에서 NuGet 다운로드 없이 빌드됩니다.

| 파일 | 출처 |
|---|---|
| Markdig.dll | Markdig 1.3.2 (lib/net10.0) |
| Microsoft.Web.WebView2.Core.dll, .WinForms.dll | Microsoft.Web.WebView2 1.0.4191.47 (lib_manual/netcoreapp3.0) |
| Microsoft.Web.WebView2.Wpf.dll | 같은 패키지 (lib_manual/net5.0-windows10.0.17763.0) |
| WebView2Loader.dll | 같은 패키지 (runtimes/win-x64/native) |
| conpty/conpty.dll, conpty/x64/OpenConsole.exe | Microsoft.Windows.Console.ConPTY 1.25.260930003 (runtimes/win-x64/native, build/native/runtimes/x64), MIT · Windows 10 내장 콘솔이 ConPTY로 들어온 한글 입력을 버리는 문제 때문에 앱 폴더의 최신 ConPTY를 씁니다 |

버전을 올릴 때는 NuGet 패키지에서 같은 경로의 파일로 교체합니다.
