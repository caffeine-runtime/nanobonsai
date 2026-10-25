#!/bin/bash
# Builds the macOS CEF bridge (UnityCefPlugin.bundle) and its helper binary on an Apple Silicon Mac against the pinned
# CEF SDK, and gathers them with the SDK's framework, license and credits into NativePlugin/mac, the payload
# Assets/Editor/CefPlayerPayload.cs puts into a Mac player; NativePlugin/cef-mac.zip carries it to a Windows machine.
# Needs Xcode's Command Line Tools and CMake. Run from the project: bash NativePlugin/build-cef-mac.sh
set -euo pipefail

CEF_VERSION="148.0.10+g7ee53f5+chromium-148.0.7778.218"
CEF_SHA1="f071b9ce7629434c5315d283e8ff1834503fc740"
CEF_NAME="cef_binary_${CEF_VERSION}_macosarm64_minimal"
NATIVE="$(cd "$(dirname "$0")" && pwd)"
CACHE="${HOME}/.cache/unity-cef"
SDK="${CACHE}/${CEF_NAME}"
BUILD="${NATIVE}/build-mac"
OUT="${NATIVE}/mac"
FRAMEWORK="Chromium Embedded Framework.framework"

[ "$(uname -m)" = "arm64" ] || { echo "This script builds for Apple Silicon and must run on an arm64 Mac."; exit 1; }
command -v cmake >/dev/null || { echo "CMake is missing: brew install cmake"; exit 1; }
xcrun --show-sdk-path >/dev/null || { echo "Xcode's Command Line Tools are missing: xcode-select --install"; exit 1; }

if [ ! -f "${SDK}/include/cef_app.h" ]; then
    mkdir -p "${CACHE}"
    archive="${CACHE}/${CEF_NAME}.tar.bz2"
    curl -fL -o "${archive}" "https://cef-builds.spotifycdn.com/${CEF_NAME//+/%2B}.tar.bz2"
    echo "${CEF_SHA1}  ${archive}" | shasum -a 1 -c -
    tar -xjf "${archive}" -C "${CACHE}"
    rm "${archive}"
fi

cmake -S "${NATIVE}" -B "${BUILD}" -DCEF_ROOT="${SDK}" -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_OSX_SYSROOT="$(xcrun --show-sdk-path)"
cmake --build "${BUILD}" --target UnityCefPlugin UnityCefHelper -j "$(sysctl -n hw.ncpu)"

rm -rf "${OUT}" "${NATIVE}/cef-mac.zip"
mkdir -p "${OUT}"
cp -R "$(find "${BUILD}" -maxdepth 2 -name UnityCefPlugin.bundle -type d | head -1)" "${OUT}/"
cp "$(find "${BUILD}" -maxdepth 2 -name UnityCefHelper -type f -perm -u+x | head -1)" "${OUT}/"
cp -R "${SDK}/Release/${FRAMEWORK}" "${OUT}/"
cp "${SDK}/LICENSE.txt" "${SDK}/CREDITS.html" "${OUT}/"
echo "${CEF_VERSION}" > "${OUT}/cef_version.txt"
lipo -archs "${OUT}/UnityCefPlugin.bundle/Contents/MacOS/UnityCefPlugin"
lipo -archs "${OUT}/UnityCefHelper"
(cd "${NATIVE}" && ditto -c -k --norsrc --noextattr --noqtn --keepParent mac cef-mac.zip)
echo "Built ${OUT} and ${NATIVE}/cef-mac.zip."
