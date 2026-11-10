# Third-party notices

File Search Manager (MIT, see `LICENSE`) incorporates or is built with the third-party
components below. Their licenses are MIT, BSD-3-Clause and Apache-2.0, all reproduced at the
end of this document. Two components carry further third-party code, whose notices ship
verbatim next to this file:

- `licenses/SkiaSharp-THIRD-PARTY-NOTICES.txt` - the native SkiaSharp and HarfBuzzSharp
  libraries: Skia, HarfBuzz, FreeType, libpng, libjpeg-turbo, zlib, piex, the Adobe DNG SDK
  and more.
- `licenses/dotnet-THIRD-PARTY-NOTICES.txt` - the .NET runtime, where a package carries it
  (the Windows installer, the portable tar.gz and the .rpm and .deb packages).

Every package the build references must be listed here: the release workflow checks it
(`tools/check-third-party-notices.py`).

## Shipped with the application

### Avalonia 12.1.6, Avalonia.Controls.DataGrid 12.1.2, Avalonia.Controls.WebView 12.1.1

The user interface: Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Skia,
Avalonia.HarfBuzz, Avalonia.X11, Avalonia.FreeDesktop, Avalonia.FreeDesktop.AtSpi,
Avalonia.Win32, Avalonia.Native, Avalonia.Remote.Protocol, Avalonia.Controls.DataGrid and
Avalonia.Controls.WebView.

- Project: https://github.com/AvaloniaUI/Avalonia, https://avaloniaui.net
- Copyright (c) The AvaloniaUI Project; Avalonia.Controls.WebView Copyright (c) AvaloniaUI OÜ
- License: MIT

### Avalonia.Angle.Windows.Natives 2.1.27548.20260419 (Windows only)

ANGLE, the OpenGL ES implementation Avalonia draws with on Windows.

- Project: https://avaloniaui.net, https://chromium.googlesource.com/angle/angle
- Copyright 2018 The ANGLE Project Authors
- License: BSD-3-Clause (reproduced below)

### SkiaSharp 3.119.4 and HarfBuzzSharp 8.3.1.3

The 2D graphics and text shaping behind Avalonia, with their native libraries
(SkiaSharp.NativeAssets.* and HarfBuzzSharp.NativeAssets.*).

- Project: https://github.com/mono/SkiaSharp
- Copyright (c) Microsoft Corporation
- License: MIT

The native libraries incorporate third-party code - among others Skia (BSD-3-Clause),
HarfBuzz (MIT), FreeType (FreeType License), libpng (libpng-2.0), libjpeg-turbo (IJG,
BSD-3-Clause, Zlib), zlib (Zlib), piex (Apache-2.0) and the Adobe DNG SDK (DNG SDK License
Agreement). This product includes DNG technology under license by Adobe Systems
Incorporated. All their notices: `licenses/SkiaSharp-THIRD-PARTY-NOTICES.txt`.

### Tmds.DBus.Protocol 0.94.1

D-Bus for Avalonia on Linux (accessibility, the desktop's dialogs and settings).

- Project: https://github.com/tmds/Tmds.DBus
- Copyright (c) Tom Deseyn
- License: MIT

### MicroCom.Runtime 0.11.6

COM interop used by Avalonia.

- Project: https://github.com/kekekeks/MicroCom
- Copyright (c) 2021 Nikita Tsukanov
- License: MIT

### Microsoft.Web.WebView2 1.0.4314.35

- Project: https://www.nuget.org/packages/Microsoft.Web.WebView2
- Copyright (C) Microsoft Corporation
- License: BSD-3-Clause (reproduced below)

Note: only the WebView2 SDK is redistributed. The Microsoft Edge WebView2 Runtime it
uses is part of Windows and is not shipped with File Search Manager. On Linux only its
managed interop assembly is shipped, and it is not used there.

### NonBlocking 2.1.2

- Project: https://github.com/VSadov/NonBlocking
- Copyright (c) 2023 Vladimir Sadov
- License: MIT

### SharpCompress 0.50.8

- Project: https://github.com/adamhathcock/sharpcompress
- Copyright (c) 2014 Adam Hathcock
- License: MIT

### singleHtmlApps (Hex Editor and Comparer, Log Explorer, Calculator)

- Project: https://github.com/KoudelkaB/singleHtmlApps
- Copyright (c) 2026 KoudelkaB
- License: MIT (also shipped as `WebApps/LICENSE.singleHtmlApps.txt`)

### System.ServiceProcess.ServiceController 10.0.14 and System.Diagnostics.EventLog 10.0.14 (Windows service only)

- Project: https://github.com/dotnet/runtime
- Copyright (c) .NET Foundation and Contributors
- License: MIT

### .NET runtime 10

The Windows installer, the portable tar.gz and the .rpm and .deb packages are published
self-contained and carry the .NET runtime. Its own third-party notices:
`licenses/dotnet-THIRD-PARTY-NOTICES.txt`.

- Project: https://github.com/dotnet/runtime
- Copyright (c) .NET Foundation and Contributors
- License: MIT

## Build tools (not shipped)

### PropertyChanged.Fody 4.1.0 and Fody 6.6.4

- Project: https://github.com/Fody/PropertyChanged, https://github.com/Fody/Fody
- Copyright (c) Simon Cropp and contributors
- License: MIT

Note: Fody and PropertyChanged.Fody are build-time IL weavers - the tools
themselves are not redistributed, but code woven by them ships in the
application binaries, so their attribution is kept here.

### Avalonia.BuildServices 11.3.2

- Project: https://avaloniaui.net
- Copyright (c) 2023-2025 The AvaloniaUI Project
- License: MIT

Note: an MSBuild task Avalonia runs while building; none of it is redistributed.

### MinVer 8.0.0

- Project: https://github.com/adamralph/minver
- Copyright (c) Adam Ralph and contributors
- License: Apache License 2.0 (reproduced below)

Note: MinVer is a build-time versioning tool (`PrivateAssets="All"`); none of
its code is redistributed with File Search Manager - its only output is the version
metadata stamped into the assemblies. It has not been modified, and its
package contains no NOTICE file. The full Apache License 2.0 text is included
below to satisfy its attribution conditions under any reading.

---

## The MIT License

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

---

## The BSD 3-Clause License

### Microsoft.Web.WebView2

```text
Copyright (C) Microsoft Corporation. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

   * Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.
   * Redistributions in binary form must reproduce the above
copyright notice, this list of conditions and the following disclaimer
in the documentation and/or other materials provided with the
distribution.
   * The name of Microsoft Corporation, or the names of its contributors 
may not be used to endorse or promote products derived from this
software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

### ANGLE (Avalonia.Angle.Windows.Natives)

```text
// Copyright 2018 The ANGLE Project Authors.
// All rights reserved.
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions
// are met:
//
//     Redistributions of source code must retain the above copyright
//     notice, this list of conditions and the following disclaimer.
//
//     Redistributions in binary form must reproduce the above
//     copyright notice, this list of conditions and the following
//     disclaimer in the documentation and/or other materials provided
//     with the distribution.
//
//     Neither the name of TransGaming Inc., Google Inc., 3DLabs Inc.
//     Ltd., nor the names of their contributors may be used to endorse
//     or promote products derived from this software without specific
//     prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
// "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS
// FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE
// COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT,
// INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING,
// BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
// LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
// CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT
// LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN
// ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
// POSSIBILITY OF SUCH DAMAGE.
```

---

## The Apache License 2.0

```text

                                 Apache License
                           Version 2.0, January 2004
                        http://www.apache.org/licenses/

   TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION

   1. Definitions.

      "License" shall mean the terms and conditions for use, reproduction,
      and distribution as defined by Sections 1 through 9 of this document.

      "Licensor" shall mean the copyright owner or entity authorized by
      the copyright owner that is granting the License.

      "Legal Entity" shall mean the union of the acting entity and all
      other entities that control, are controlled by, or are under common
      control with that entity. For the purposes of this definition,
      "control" means (i) the power, direct or indirect, to cause the
      direction or management of such entity, whether by contract or
      otherwise, or (ii) ownership of fifty percent (50%) or more of the
      outstanding shares, or (iii) beneficial ownership of such entity.

      "You" (or "Your") shall mean an individual or Legal Entity
      exercising permissions granted by this License.

      "Source" form shall mean the preferred form for making modifications,
      including but not limited to software source code, documentation
      source, and configuration files.

      "Object" form shall mean any form resulting from mechanical
      transformation or translation of a Source form, including but
      not limited to compiled object code, generated documentation,
      and conversions to other media types.

      "Work" shall mean the work of authorship, whether in Source or
      Object form, made available under the License, as indicated by a
      copyright notice that is included in or attached to the work
      (an example is provided in the Appendix below).

      "Derivative Works" shall mean any work, whether in Source or Object
      form, that is based on (or derived from) the Work and for which the
      editorial revisions, annotations, elaborations, or other modifications
      represent, as a whole, an original work of authorship. For the purposes
      of this License, Derivative Works shall not include works that remain
      separable from, or merely link (or bind by name) to the interfaces of,
      the Work and Derivative Works thereof.

      "Contribution" shall mean any work of authorship, including
      the original version of the Work and any modifications or additions
      to that Work or Derivative Works thereof, that is intentionally
      submitted to Licensor for inclusion in the Work by the copyright owner
      or by an individual or Legal Entity authorized to submit on behalf of
      the copyright owner. For the purposes of this definition, "submitted"
      means any form of electronic, verbal, or written communication sent
      to the Licensor or its representatives, including but not limited to
      communication on electronic mailing lists, source code control systems,
      and issue tracking systems that are managed by, or on behalf of, the
      Licensor for the purpose of discussing and improving the Work, but
      excluding communication that is conspicuously marked or otherwise
      designated in writing by the copyright owner as "Not a Contribution."

      "Contributor" shall mean Licensor and any individual or Legal Entity
      on behalf of whom a Contribution has been received by Licensor and
      subsequently incorporated within the Work.

   2. Grant of Copyright License. Subject to the terms and conditions of
      this License, each Contributor hereby grants to You a perpetual,
      worldwide, non-exclusive, no-charge, royalty-free, irrevocable
      copyright license to reproduce, prepare Derivative Works of,
      publicly display, publicly perform, sublicense, and distribute the
      Work and such Derivative Works in Source or Object form.

   3. Grant of Patent License. Subject to the terms and conditions of
      this License, each Contributor hereby grants to You a perpetual,
      worldwide, non-exclusive, no-charge, royalty-free, irrevocable
      (except as stated in this section) patent license to make, have made,
      use, offer to sell, sell, import, and otherwise transfer the Work,
      where such license applies only to those patent claims licensable
      by such Contributor that are necessarily infringed by their
      Contribution(s) alone or by combination of their Contribution(s)
      with the Work to which such Contribution(s) was submitted. If You
      institute patent litigation against any entity (including a
      cross-claim or counterclaim in a lawsuit) alleging that the Work
      or a Contribution incorporated within the Work constitutes direct
      or contributory patent infringement, then any patent licenses
      granted to You under this License for that Work shall terminate
      as of the date such litigation is filed.

   4. Redistribution. You may reproduce and distribute copies of the
      Work or Derivative Works thereof in any medium, with or without
      modifications, and in Source or Object form, provided that You
      meet the following conditions:

      (a) You must give any other recipients of the Work or
          Derivative Works a copy of this License; and

      (b) You must cause any modified files to carry prominent notices
          stating that You changed the files; and

      (c) You must retain, in the Source form of any Derivative Works
          that You distribute, all copyright, patent, trademark, and
          attribution notices from the Source form of the Work,
          excluding those notices that do not pertain to any part of
          the Derivative Works; and

      (d) If the Work includes a "NOTICE" text file as part of its
          distribution, then any Derivative Works that You distribute must
          include a readable copy of the attribution notices contained
          within such NOTICE file, excluding those notices that do not
          pertain to any part of the Derivative Works, in at least one
          of the following places: within a NOTICE text file distributed
          as part of the Derivative Works; within the Source form or
          documentation, if provided along with the Derivative Works; or,
          within a display generated by the Derivative Works, if and
          wherever such third-party notices normally appear. The contents
          of the NOTICE file are for informational purposes only and
          do not modify the License. You may add Your own attribution
          notices within Derivative Works that You distribute, alongside
          or as an addendum to the NOTICE text from the Work, provided
          that such additional attribution notices cannot be construed
          as modifying the License.

      You may add Your own copyright statement to Your modifications and
      may provide additional or different license terms and conditions
      for use, reproduction, or distribution of Your modifications, or
      for any such Derivative Works as a whole, provided Your use,
      reproduction, and distribution of the Work otherwise complies with
      the conditions stated in this License.

   5. Submission of Contributions. Unless You explicitly state otherwise,
      any Contribution intentionally submitted for inclusion in the Work
      by You to the Licensor shall be under the terms and conditions of
      this License, without any additional terms or conditions.
      Notwithstanding the above, nothing herein shall supersede or modify
      the terms of any separate license agreement you may have executed
      with Licensor regarding such Contributions.

   6. Trademarks. This License does not grant permission to use the trade
      names, trademarks, service marks, or product names of the Licensor,
      except as required for reasonable and customary use in describing the
      origin of the Work and reproducing the content of the NOTICE file.

   7. Disclaimer of Warranty. Unless required by applicable law or
      agreed to in writing, Licensor provides the Work (and each
      Contributor provides its Contributions) on an "AS IS" BASIS,
      WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or
      implied, including, without limitation, any warranties or conditions
      of TITLE, NON-INFRINGEMENT, MERCHANTABILITY, or FITNESS FOR A
      PARTICULAR PURPOSE. You are solely responsible for determining the
      appropriateness of using or redistributing the Work and assume any
      risks associated with Your exercise of permissions under this License.

   8. Limitation of Liability. In no event and under no legal theory,
      whether in tort (including negligence), contract, or otherwise,
      unless required by applicable law (such as deliberate and grossly
      negligent acts) or agreed to in writing, shall any Contributor be
      liable to You for damages, including any direct, indirect, special,
      incidental, or consequential damages of any character arising as a
      result of this License or out of the use or inability to use the
      Work (including but not limited to damages for loss of goodwill,
      work stoppage, computer failure or malfunction, or any and all
      other commercial damages or losses), even if such Contributor
      has been advised of the possibility of such damages.

   9. Accepting Warranty or Additional Liability. While redistributing
      the Work or Derivative Works thereof, You may choose to offer,
      and charge a fee for, acceptance of support, warranty, indemnity,
      or other liability obligations and/or rights consistent with this
      License. However, in accepting such obligations, You may act only
      on Your own behalf and on Your sole responsibility, not on behalf
      of any other Contributor, and only if You agree to indemnify,
      defend, and hold each Contributor harmless for any liability
      incurred by, or claims asserted against, such Contributor by reason
      of your accepting any such warranty or additional liability.

   END OF TERMS AND CONDITIONS

   APPENDIX: How to apply the Apache License to your work.

      To apply the Apache License to your work, attach the following
      boilerplate notice, with the fields enclosed by brackets "[]"
      replaced with your own identifying information. (Don't include
      the brackets!)  The text should be enclosed in the appropriate
      comment syntax for the file format. We also recommend that a
      file or class name and description of purpose be included on the
      same "printed page" as the copyright notice for easier
      identification within third-party archives.

   Copyright [yyyy] [name of copyright owner]

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
```
