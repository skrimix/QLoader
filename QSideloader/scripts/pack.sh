#!/bin/sh

echo "Deleting old archives"
rm win-x64.zip
rm linux-x64.tar.gz
rm osx-x64.zip
#rm osx-arm64.zip
rm TrailersAddon.zip

echo "Packing win-x64 build"
zip -r win-x64.zip win-x64

echo "Packing linux-x64 build"
chmod +x linux-x64/Loader
chmod -R +x linux-x64/tools/
tar cvzf linux-x64.tar.gz linux-x64

echo "Packing linux-arm64 build"
chmod +x linux-arm64/Loader
chmod -R +x linux-arm64/tools/
tar cvzf linux-arm64.tar.gz linux-arm64

echo "Packing osx-x64 build"
chmod +x osx-x64/Loader
chmod -R +x osx-x64/tools/
zip -r osx-x64.zip osx-x64

# chmod +x osx-arm64/Loader
# chmod -R +x osx-arm64/tools/
# # Check if rcodesign tool is installed
# if ! [ -x "$HOME/.cargo/bin/rcodesign" ]; then
#   echo 'Error: rcodesign is not installed.' >&2
#   echo 'Please install apple-codesign rust crate.' >&2
#   exit 1
# fi
# # MacOS doesn't allow to run unsigned native M1 binaries
# echo "Signing osx-arm64 build with ad-hoc certificate"
# $HOME/.cargo/bin/rcodesign sign osx-arm64/Loader
# find osx-arm64 -name "*.dylib" -exec $HOME/.cargo/bin/rcodesign sign {} \;
# echo "Packing osx-arm64 build"
# zip -r osx-arm64.zip osx-arm64

echo "Packing trailers addon"
CURRENTDIR=$(pwd)
cd ../../../
zip -r "$CURRENTDIR/TrailersAddon.zip" Resources/videos/
cd "$CURRENTDIR" || exit

echo "Done!"