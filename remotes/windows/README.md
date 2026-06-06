# JnapUpMon Remote for Windows

## Build

### x64
```
dotnet publish JnapUpMon.Remote\JnapUpMon.Remote.csproj --configuration Release -r win-x64 --self-contained true -o publish\win-x64
```

### ARM64
```
dotnet publish JnapUpMon.Remote\JnapUpMon.Remote.csproj --configuration Release -r win-arm64 --self-contained true -o publish\win-arm64
```
