# NuGet package

Package ID: `RZeroF1.Minecraft.Projection`

Version: `0.1.0`

Target framework: `net10.0`

Native runtime: `win-x64`

The package contains the managed SDK assembly, `runtimes/win-x64/native/` renderer DLL, README, API and architecture documents, source and third-party notices, and license texts. It has no UI framework runtime dependency.

Build and pack on Windows x64 with .NET 10 and Visual Studio C++ build tools:

```powershell
dotnet build src/RZeroF1.Minecraft.Projection/RZeroF1.Minecraft.Projection.csproj -c Release -r win-x64
dotnet pack src/RZeroF1.Minecraft.Projection/RZeroF1.Minecraft.Projection.csproj -c Release -r win-x64
```

The native project downloads only the pinned WinUI ABI header package during restore. The resulting DLL is included in the package's native runtime folder. The independent package consumer restores from `artifacts/packages` without a project reference and verifies the packaged API and Native DLL.

The package declares the MPL-2.0 license expression and includes the complete license text together with third-party MIT notices.
