# Third-party notices

Ivalice Trainer does not distribute any game content. Job, item, ability and unit names are read at runtime from the game installed on the player's machine. The DirectStorage native libraries used for decompression (`dstorage.dll`, `dstoragecore.dll`) are loaded from the game's install folder and are not redistributed.

## Third-party code included in the source and the program

| Component | Location | License | Copyright |
|---|---|---|---|
| FF16Tools 1.13.3 (source subset with modifications; see the README in that folder) | `src/FftTrainer.GameFiles/ThirdParty/FF16Tools/` | MIT (full text in `LICENSE.txt` in the same folder) | Copyright (c) 2025 Nenkai |
| fftivc-nex-layouts (6 table layouts) | `src/FftTrainer.GameFiles/Nex/Layouts/ffto/` | MIT | Copyright (c) Nenkai and contributors, https://github.com/Nenkai/fftivc-nex-layouts |

## NuGet dependencies (managed DLLs shipped with the program)

| Package | Version | License | Copyright |
|---|---|---|---|
| CommunityToolkit.HighPerformance | 8.4.0 | MIT | (c) .NET Foundation and Contributors |
| Syroot.BinaryData / Syroot.BinaryData.Memory / Syroot.BinaryData.Core | 5.2.2 / 5.2.2 / 5.2.0 | MIT | (c) Syroot |
| System.IO.Hashing | 10.0.1 | MIT | © Microsoft Corporation |
| Vortice.DirectStorage / Vortice.Direct3D12 / Vortice.DXGI / Vortice.DirectX | 3.8.3 | MIT | Copyright (c) Amer Koleci and Contributors |
| Vortice.Mathematics | 2.1.0 | MIT | Copyright (c) Amer Koleci and Contributors |
| SharpGen.Runtime / SharpGen.Runtime.COM | 2.4.2-beta | MIT | (c) 2010-2017 Alexandre Mutel, 2017-2023 Jeremy Koritzinsky, 2023-2024 Amer Koleci |

Only the managed part of Vortice.DirectStorage is referenced (`ExcludeAssets="native"`); the Microsoft DirectStorage native libraries it carries are not copied to the output.

## MIT License (terms shared by the components above)

```
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
