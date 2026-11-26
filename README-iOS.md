# OpenUtau Mobile for iOS

This branch (`ios-port`) adds an iOS build of OpenUtau Mobile 1.1.7. The app is
.NET MAUI, so almost all code is shared with Android; the iOS-specific work is:

| Area | Android | iOS (this port) |
|---|---|---|
| Native resampler / vocoder (`libworldline`) | prebuilt `.so` per ABI | `worldline.xcframework`, built from OpenUtau 0.1.565's C++ by `native/ios/build_worldline_ios.sh` |
| Audio playback | `AudioTrack` (`AudioTrackOutput.cs`) | `AVAudioEngine` (`Platforms/iOS/Utils/Audio/AVAudioEngineOutput.cs`); upstream had a silent placeholder |
| Opening files | in-app browser over `/storage/emulated/0` | system document picker (Files, iCloud Drive, other apps); the file is copied into the app (`IosFileImporter.cs`) |
| Saving / exporting | in-app browser | in-app browser rooted at the app's Documents folder, which the Files app shows as **On My iPhone › OpenUtau** |
| ONNX Runtime (DiffSinger, Vogen, …) | 1.16.3 native | 1.22.1 native, matching the 1.22.1 managed bindings `OpenUtau.Core` already uses |
| Pitch magnifier while drawing | `Android.Widget.Magnifier` | not available (no iOS equivalent in MAUI) |

## Installing

There is no App Store build. CI produces an **unsigned** IPA
(`OpenUtauMobile-<version>-ios-unsigned.ipa`, under the **iOS** workflow's
artifacts). Sign and install it with any sideloading tool, e.g.
[AltStore](https://altstore.io), [SideStore](https://sidestore.io), or
Sideloadly, using a free or paid Apple ID. With a free Apple ID the app must be
re-signed every 7 days.

Requires iOS 15 or later and an arm64 device (iPhone 6s / iPad 5th gen or newer).

## Using it

1. Download a singer (usually a `.zip`) to Files or iCloud Drive.
2. **Singers › +** opens the iOS file picker. Pick the archive and follow the installer.
3. **Open** on the home screen also uses the file picker. Projects you open
   from elsewhere are copied to *On My iPhone › OpenUtau › Imported*. Edit and
   save that copy (autosaves sit next to it).
4. Exported WAVs and saved projects go to the app's Documents folder. Open the
   Files app and look under *On My iPhone › OpenUtau* to share them.

## Building

You need a Mac with Xcode 16+ and the .NET 9 SDK.

```sh
dotnet workload install maui-ios
bash native/ios/build_worldline_ios.sh          # -> OpenUtauMobile/Platforms/iOS/Native/worldline.xcframework

# simulator
dotnet build OpenUtauMobile/OpenUtauMobile.csproj -f net9.0-ios -p:IosOnly=true -c Release -r iossimulator-arm64

# device: signs with your development team, or pass -p:EnableCodeSigning=false for an unsigned .app
dotnet build OpenUtauMobile/OpenUtauMobile.csproj -f net9.0-ios -p:IosOnly=true -c Release -r ios-arm64
```

`.github/workflows/ios.yml` does the same on a GitHub macOS runner. It also
launches the app in a simulator and checks the `[ios-port]` self-test lines the
app prints at startup: worldline loads, the P/Invoke path runs real F0
analysis, and ONNX Runtime initialises. It uploads a screenshot and logs as
artifacts.

### Native library notes

Upstream builds worldline with Bazel. The iOS script compiles the same source
set with `clang` so it can target the iOS SDKs. The pinned dependencies and
patches match `cpp/WORKSPACE.bazel` at OpenUtau `0.1.565`, which is the
version `OpenUtau.Core` here was forked from. Its P/Invoke signatures match
that version exactly; newer upstream worldline has a different ABI. Differences
from the desktop build:

* `audio_output.cc` (miniaudio) is left out. `MiniAudioOutput` marshals an
  instance delegate as a native callback, which iOS full-AOT does not support,
  so playback uses AVAudioEngine instead.
* abseil is replaced by `native/ios/shim/absl/strings/numbers.h`, because only
  `SimpleAtoi`/`SimpleAtod` are used.
