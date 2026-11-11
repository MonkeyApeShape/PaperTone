$ErrorActionPreference = "Stop"
dotnet publish .\PaperTone\PaperTone.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
  -o .\publish
Write-Host "`nBuild complete: .\publish\PaperTone.exe"
