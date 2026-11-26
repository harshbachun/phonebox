# Deskmate

My desk phone panel: a Galaxy A52 on the desk, showing what my Windows PC is doing and controlling it.

- **Phone app** (`android/`): a full-screen Android app (`app.deskmate.panel`). The panel itself is plain HTML/JS in `android/app/src/main/assets/www`.
- **PC agent** (`agent/Deskmate.Agent`): a .NET 10 Windows tray app (`DeskmateAgent.exe`). It feeds the phone over USB (`adb reverse`, port 8765) or Wi-Fi (HTTPS, port 8766) and serves the settings page at http://127.0.0.1:8765.

Pages: Home, AI quotas (bars), Sessions, System, Processes, Shortcuts, Discord (voice controls, my five servers), VALORANT (match status, Save clip, Hold to talk, mic mute, post-match stats) and Music.

## Version

1.0.0. Deskmate has its own version numbers and does not check for or install updates from anywhere: it is built from this source.

## Build

```powershell
# phone app (JDK 17)
$env:JAVA_HOME = 'C:/Users/harsh/.jdks/jdk-17.0.21.1+2'; $env:ANDROID_HOME = "$env:LOCALAPPDATA\Android\Sdk"
cd android; .\gradlew.bat assembleDebug   # then: adb install -r app\build\outputs\apk\debug\app-debug.apk

# PC agent
dotnet publish agent/Deskmate.Agent -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o <folder>
```

The agent lives in `%LOCALAPPDATA%\Deskmate\app` and keeps its settings in `%APPDATA%\Deskmate\config.json`.

## Credit

Deskmate started from [PhoneBox](https://github.com/ersinkoc/phonebox) by Ersin Koç (MIT license, see [LICENSE](LICENSE)). It is now developed as its own project, and PhoneBox serves only as a reference. The other documents in `docs/` and `CHANGELOG.md` are PhoneBox's and describe its history up to 0.9.24.
