# FF16Tools (source subset)

Source: https://github.com/Nenkai/FF16Tools, tag 1.13.3, MIT License (see `LICENSE.txt` in this folder, Copyright (c) 2025 Nenkai).

Only the files needed to read the pack files, Nex tables and UI textures of FINAL FANTASY TACTICS - The Ivalice Chronicles were copied:

| Folder | Original path |
|---|---|
| `Pack/` | `FF16Tools.Pack/FF16Pack.cs`, `FF16PackFile.cs`, `FF16PackDStorageChunk.cs`, `FF16PackPathUtil.cs`, `FF16UnpackOptions.cs`, `Crypto/*`, `Hashing/*` |
| `Shared/` | `FF16Tools.Shared/GDeflate.cs` |
| `Files/Textures/` | `FF16Tools.Files/Textures/TextureFile.cs`, `TextureInfo.cs`, `TextureChunkInfo.cs`, `TextureUtils.cs` |
| `Shared/Dds/` | `FF16Tools.Shared/Dds/DdsHeader.cs`, `DxgiUtils.cs`, `Enums.cs` |
| `Files/Nex/` | `FF16Tools.Files/Nex/NexDataFile.cs`, `TableMappingReader.cs`, `NexUtils.cs`, `NexEnums.cs`, `Entities/*`, `Managers/*` |

Changes:

- `TextureFile.cs`: removed the `Microsoft.Extensions.Logging` constructor, fields and log statements, and the unused ImageSharp / BCnEncoder imports (image conversion is done by `PortraitExtractor` instead).
- `FF16Pack.cs` and `FF16UnpackOptions.cs`: removed the `Microsoft.Extensions.Logging` parameters and log statements.
- `NexUnionKey.cs`: removed the read / write methods that depend on `SmartBinaryStream` (only used when writing tables).

Everything else is unchanged.
