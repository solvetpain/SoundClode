@echo off
echo Building SoundClode Native Client...
dotnet publish SoundCloudNative/SoundCloudNative.csproj -c Release -o publish
echo Done! Run publish\SoundClode.exe
pause
