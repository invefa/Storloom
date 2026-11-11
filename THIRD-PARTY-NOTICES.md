# Third-party notices

Storloom is MIT licensed. The following components retain their original licenses.

## Archive query storage

- Microsoft.Data.Sqlite.Core 10.0.14: https://github.com/dotnet/efcore/tree/v10.0.14, MIT.
- SQLite3MC.PCLRaw.bundle/provider/lib 2.6.1: https://github.com/utelle/SQLite3MultipleCiphers-NuGet, package source revision ce660b9f2a1ee2aa2d20feda926cf6bf4477ad77, MIT.
- SQLite3 Multiple Ciphers 2.6.1: https://github.com/utelle/SQLite3MultipleCiphers/tree/v2.6.1, based on SQLite 3.54.0. SQLite is public domain; encryption extension is MIT, with retained component notices.
- SQLitePCLRaw.core 3.0.2: https://github.com/ericsink/SQLitePCL.raw/tree/v3.0.2, Apache-2.0.

These dependencies are isolated from the system SQLite source adapters. Query data and postings can be encrypted with the native ChaCha20-Poly1305 cipher. Package versions are pinned; existing archive formats are not converted automatically. Full licenses and native component notices are in docs/licenses in the source tree and licenses/ArchiveStorage in distribution packages.

## chatlog-keeper

Source: https://github.com/labazhou2024/chatlog-keeper
Revision: 2225294fd0761e10ce15540f21e7dbb2ac304c3c

The Windows WeChat and NTQQ C# debugger capture components were adapted from the upstream PowerShell-embedded sources into src/Storloom.Core/Chats/NativeCapture/WeChatCapture.cs and QQCapture.cs. The NTQQ adapter removes key logging, checks the signed module path and code bytes, honors the key-length argument, and accepts candidates only after authenticating the selected backup. SQLCipher/NTQQ compatibility and message-format research also informed the independent C# pipeline. Changes include namespace/API integration, removal of diagnostic calibration hooks, no-op diagnostic callbacks, signed-client validation, isolated worker lifecycle, and in-memory key handling. No Python runtime or upstream executable is bundled.

MIT License

Copyright (c) 2026 labazhou2024

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

## ZstdSharp.Port 0.8.6

Source: https://github.com/oleg-st/ZstdSharp
Package source revision: f475a7d4746d305094fbb09563cb0fcb167bec18
Used for Zstandard-compressed message bodies.

MIT License

Copyright (c) 2021 Oleg Stepanischev

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

## Zstandard

ZstdSharp is a C# port of Zstandard. Upstream: https://github.com/facebook/zstd
The upstream BSD license is reproduced below (v1.5.7).

BSD License

For Zstandard software

Copyright (c) Meta Platforms, Inc. and affiliates. All rights reserved.

Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

 * Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

 * Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

 * Neither the name Facebook, nor Meta, nor the names of its contributors may
   be used to endorse or promote products derived from this software without
   specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON
ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

## Microsoft .NET

Self-contained distributions include Microsoft .NET runtime binaries. The exact restored runtime packs' LICENSE and available THIRD-PARTY-NOTICES files are copied into `licenses/<framework>/` under each published application directory. Sources: https://github.com/dotnet/runtime and https://github.com/dotnet/winforms (MIT and third-party terms). The Windows SQLite system component is provided by the operating system and is not redistributed here.

## Microsoft Edge WebView2 SDK

The Windows application includes the Microsoft.Web.WebView2 managed SDK and loader for the optional embedded archive reader. The exact restored SDK's LICENSE.txt and NOTICE.txt are included under licenses/Microsoft.Web.WebView2 in release packages. The Microsoft Edge WebView2 Runtime is installed separately and is not bundled. Source and terms: https://www.nuget.org/packages/Microsoft.Web.WebView2 and https://learn.microsoft.com/microsoft-edge/webview2/.

## WPF UI 4.3.0

Source: https://github.com/lepoco/wpfui
Used by the native desktop frontend. Release packages include the exact restored LICENSE.md and embedded-component ThirdPartyNotices.txt under licenses/WPF-UI/.

MIT License

Copyright (c) 2021-2025 Leszek Pomianowski and WPF UI Contributors. https://lepo.co/

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

## K4os.Compression.LZ4 1.3.8

Source: https://github.com/MiloszKrajewski/K4os.Compression.LZ4/tree/1.3.8
Used to read legacy WeChat LZ4 message bodies.

MIT License

Copyright (c) 2017 Milosz Krajewski

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

## ChatTrace media format research

Source: https://github.com/qiaodogbear/ChatTrace/tree/f9fcdb42e8fb99a527c0bcd7d6ff7ea4bc891563
WeChat V1/V2 image format and offline key derivation informed the C# decoder. Python code is not bundled.

MIT License

Copyright (c) 2026 Qiao Xinliang (qiaodogbear)

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

## SILK v3 decoder

Source: https://github.com/kn007/silk-v3-decoder/tree/507be6bca8ce1fb977a061481f1d79e8c610e309
The separately compiled Windows helper uses the fixed-point SDK and Decoder sample. The pinned archive is verified by scripts/Build-Silk.ps1. Release packages retain licenses/SILK/MIT.txt and licenses/SILK/Skype-SDK.txt. No FFmpeg executable is redistributed.
The MIT License (MIT)

Copyright (c) 2020 Karl Chen

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
/***********************************************************************
Copyright (c) 2006-2012, Skype Limited. All rights reserved.
Redistribution and use in source and binary forms, with or without
modification, (subject to the limitations in the disclaimer below)
are permitted provided that the following conditions are met:
- Redistributions of source code must retain the above copyright notice,
this list of conditions and the following disclaimer.
- Redistributions in binary form must reproduce the above copyright
notice, this list of conditions and the following disclaimer in the
documentation and/or other materials provided with the distribution.
- Neither the name of Skype Limited, nor the names of specific
contributors, may be used to endorse or promote products derived from
this software without specific prior written permission.
NO EXPRESS OR IMPLIED LICENSES TO ANY PARTY'S PATENT RIGHTS ARE GRANTED
BY THIS LICENSE. THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND
CONTRIBUTORS ''AS IS'' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING,
BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND
FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE
COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT,
INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT
NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF
USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON
ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
***********************************************************************/

WeChat emoticon cache derivation and WXGF conversion research: https://github.com/yanyanbabyi/Wechat-Emoticon-Parser-fix . Storloom implements local AES-CBC decoding in C# and does not bundle the Python exporter or Tencent resources.
