#!/usr/bin/env bash
# Builds libworldline (OpenUtau's native resampler / WORLD vocoder, v0.1.565)
# for iOS as a dynamic framework packaged in an xcframework:
#
#   OpenUtauMobile/Platforms/iOS/Native/worldline.xcframework
#     ios-arm64/worldline.framework                 (devices)
#     ios-arm64_x86_64-simulator/worldline.framework (simulators)
#
# Requires macOS with Xcode command line tools. Upstream builds worldline with
# Bazel; this script compiles the same source set directly with clang so that
# it can target the iOS SDKs. Differences from the desktop build:
#   * audio_output.cc (miniaudio) is left out: iOS playback goes through
#     AVAudioEngine in C# (Platforms/iOS/Utils/Audio/AVAudioEngineOutput.cs).
#   * abseil is replaced by a tiny header shim (shim/absl/strings/numbers.h)
#     because only SimpleAtoi / SimpleAtod are used.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
SRC="$REPO/native/worldline"
WORK="${WORK_DIR:-$HERE/build}"
DEPS="$WORK/deps"
OUT="${OUT_DIR:-$REPO/OpenUtauMobile/Platforms/iOS/Native}"
MIN_IOS="${MIN_IOS:-15.0}"


mkdir -p "$DEPS"

# ---------------------------------------------------------------- deps ------
# Same pinned revisions and patches as upstream cpp/WORKSPACE.bazel @ 0.1.565.
fetch() { # name url strip_prefix [patch]
  local name="$1" url="$2" prefix="$3" patch="${4:-}"
  if [[ -d "$DEPS/$name" ]]; then return; fi
  echo "==> fetching $name"
  local tmp; tmp="$(mktemp -d)"
  curl -fsSL "$url" -o "$tmp/src.zip"
  unzip -q "$tmp/src.zip" -d "$tmp"
  mv "$tmp/$prefix" "$DEPS/$name"
  rm -rf "$tmp"
  if [[ -n "$patch" ]]; then
    (cd "$DEPS/$name" && patch -p1 --quiet < "$SRC/third_party/$patch")
  fi
}

fetch libgvps https://github.com/Sleepwalking/libgvps/archive/2f1b4106d72f8f8138dc447bf0123820c0772cbd.zip \
  libgvps-2f1b4106d72f8f8138dc447bf0123820c0772cbd
fetch libnpy https://github.com/llohse/libnpy/archive/refs/tags/v1.0.1.zip libnpy-1.0.1
fetch libpyin https://github.com/Sleepwalking/libpyin/archive/b38135390b335c3e8cea6ef35cf5093789b36dac.zip \
  libpyin-b38135390b335c3e8cea6ef35cf5093789b36dac libpyin.patch
fetch spline https://github.com/ttk592/spline/archive/5894beaf91e9adbfdbe5c6c9a1c60770e380e8e8.zip \
  spline-5894beaf91e9adbfdbe5c6c9a1c60770e380e8e8 spline.patch
fetch world https://github.com/mmorise/World/archive/f8dd5fb289db6a7f7f704497752bf32b258f9151.zip \
  World-f8dd5fb289db6a7f7f704497752bf32b258f9151 world.patch

# ------------------------------------------------------------- sources ------
W="$SRC/worldline"
CXX_SOURCES=(
  "$W/worldline.cpp"
  "$W/phrase_synth.cpp"
  "$W/classic/classic_args.cpp"
  "$W/classic/frq.cpp"
  "$W/classic/resampler.cpp"
  "$W/classic/timing.cpp"
  "$W/common/timer.cpp"
  "$W/common/vec_utils.cpp"
  "$W/f0/dio_estimator.cpp"
  "$W/f0/dio_ss_estimator.cpp"
  "$W/f0/frq_estimator.cpp"
  "$W/f0/pyin_estimator.cpp"
  "$W/model/effects.cpp"
  "$W/model/model.cpp"
  "$W/platinum/platinum.cpp"
  "$W/platinum/synthesisplatinum.cpp"
  "$DEPS/world/tools/audioio.cpp"
)
for f in "$DEPS"/world/src/*.cpp; do CXX_SOURCES+=("$f"); done

C_SOURCES=(
  "$DEPS/libpyin/math-funcs.c"
  "$DEPS/libpyin/pyin.c"
  "$DEPS/libpyin/yin.c"
)
for f in "$DEPS"/libgvps/*.c; do C_SOURCES+=("$f"); done

# libgvps is included as <libgvps/gvps.h>, so its *parent* dir is on the path.
INCLUDES=(
  -I"$HERE/shim"
  -I"$SRC"
  -I"$DEPS"
  -I"$DEPS/libgvps"
  -I"$DEPS/libpyin"
  -I"$DEPS/libnpy/include"
  -I"$DEPS/spline/src"
  -I"$DEPS/world/src"
  -I"$DEPS/world/tools"
)
DEFINES=(-DFP_TYPE=double -DNDEBUG)

# --------------------------------------------------------------- build ------
build_slice() { # sdk arch min_flag
  local sdk="$1" arch="$2" minflag="$3"
  local sysroot; sysroot="$(xcrun --sdk "$sdk" --show-sdk-path)"
  local obj="$WORK/obj/$sdk-$arch"
  mkdir -p "$obj"
  local common=(-arch "$arch" -isysroot "$sysroot" "$minflag=$MIN_IOS"
                -O2 -fPIC -fvisibility=hidden
                "${INCLUDES[@]}" "${DEFINES[@]}")
  local cxx; cxx="$(xcrun -f clang++)"
  local cc; cc="$(xcrun -f clang)"
  echo "==> compiling $sdk/$arch (${#CXX_SOURCES[@]} C++ + ${#C_SOURCES[@]} C files)"
  local src
  for src in "${CXX_SOURCES[@]}"; do
    compile_one "$obj" "$src" "$cxx" -std=c++17 -fvisibility-inlines-hidden "${common[@]}"
  done
  for src in "${C_SOURCES[@]}"; do
    compile_one "$obj" "$src" "$cc" -std=gnu11 "${common[@]}"
  done

  echo "==> linking $sdk/$arch"
  "$(xcrun -f clang++)" -dynamiclib -arch "$arch" -isysroot "$sysroot" "$minflag=$MIN_IOS" \
    -install_name @rpath/worldline.framework/worldline \
    -Wl,-dead_strip -o "$WORK/libworldline-$sdk-$arch.dylib" "$obj"/*.o
}

compile_one() { # obj_dir src compiler flags...
  local objdir="$1" src="$2" compiler="$3"; shift 3
  # Unique object name: path hash + basename (several dirs share basenames).
  local base; base="$(basename "$src")"
  local hash; hash="$(printf '%s' "$src" | shasum | cut -c1-8)"
  "$compiler" "$@" -c "$src" -o "$objdir/${base%.*}-$hash.o"
}

make_framework() { # dylib dest_dir platform
  local dylib="$1" dest="$2" platform="$3"
  local fw="$dest/worldline.framework"
  rm -rf "$fw"; mkdir -p "$fw"
  cp "$dylib" "$fw/worldline"
  cat > "$fw/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key><string>en</string>
  <key>CFBundleExecutable</key><string>worldline</string>
  <key>CFBundleIdentifier</key><string>com.openutau.worldline</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>CFBundleName</key><string>worldline</string>
  <key>CFBundlePackageType</key><string>FMWK</string>
  <key>CFBundleShortVersionString</key><string>0.1.565</string>
  <key>CFBundleVersion</key><string>565</string>
  <key>CFBundleSupportedPlatforms</key><array><string>$platform</string></array>
  <key>MinimumOSVersion</key><string>$MIN_IOS</string>
</dict>
</plist>
PLIST
}

rm -rf "$WORK/obj" "$WORK"/libworldline-*.dylib "$WORK/fw"
build_slice iphoneos        arm64  -miphoneos-version-min
build_slice iphonesimulator arm64  -mios-simulator-version-min
build_slice iphonesimulator x86_64 -mios-simulator-version-min

lipo -create "$WORK/libworldline-iphonesimulator-arm64.dylib" \
             "$WORK/libworldline-iphonesimulator-x86_64.dylib" \
     -output "$WORK/libworldline-iphonesimulator.dylib"

make_framework "$WORK/libworldline-iphoneos-arm64.dylib"   "$WORK/fw/device"    iPhoneOS
make_framework "$WORK/libworldline-iphonesimulator.dylib"  "$WORK/fw/simulator" iPhoneSimulator

# Sanity check: every symbol the C# side P/Invokes must be exported.
REQUIRED=(F0 DecodeMgc DecodeBap WorldSynthesis Resample PhraseSynthNew PhraseSynthDelete
          PhraseSynthAddRequest PhraseSynthSetCurves PhraseSynthSynth)
EXPORTS="$(nm -gU "$WORK/fw/device/worldline.framework/worldline")"
for s in "${REQUIRED[@]}"; do
  grep -q " _$s\$" <<<"$EXPORTS" || { echo "missing export: $s" >&2; exit 1; }
done
echo "==> all ${#REQUIRED[@]} required exports present"

mkdir -p "$OUT"
rm -rf "$OUT/worldline.xcframework"
xcodebuild -create-xcframework \
  -framework "$WORK/fw/device/worldline.framework" \
  -framework "$WORK/fw/simulator/worldline.framework" \
  -output "$OUT/worldline.xcframework"
echo "==> wrote $OUT/worldline.xcframework"
