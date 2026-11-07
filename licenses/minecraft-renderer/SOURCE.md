# MinecraftRenderer parser subset

- Project: `ptlthg/MinecraftRenderer`
- Fixed commit: `ae9be4a076f6f1fb1b8dba6a26c138b3434e67fa`
- License: MIT, reproduced in `LICENSE`
- Source files: `ThirdParty/MinecraftRenderer/Nbt/*.cs` and `ThirdParty/MinecraftRenderer/Schematics/*.cs`

These files provide NBT and Litematic parsing. Projection model conversion is implemented separately in the SDK.

Local changes include bounded/cancellable binary NBT parsing, duplicate-key validation, payload budgets, and Litematic block entity coordinate, overlap and provenance handling. The fixed upstream revision and MIT license above remain the source attribution for the parser subset.
