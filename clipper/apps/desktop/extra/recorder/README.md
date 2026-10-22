# Bundled recorder

`electron-builder` copies this folder to `resources/recorder/`. For a Windows
installer, publish the recorder here first:

```
dotnet publish ../../recorder/GamerPlug.Recorder/GamerPlug.Recorder.csproj \
  -c Release -r win-x64 --self-contained false -o extra/recorder
```

Then drop an LGPL `ffmpeg.exe` (and its DLLs) next to `GamerPlug.Recorder.exe`.
Override at runtime with `GAMERPLUG_RECORDER_EXE` / `GAMERPLUG_FFMPEG`.
