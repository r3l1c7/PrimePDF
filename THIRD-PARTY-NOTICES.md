# Third-party notices

Prime PDF is distributed as a self-contained executable, which means the components
below are bundled inside the binary you ship. Their licence terms travel with it.

All of them are permissive and compatible with this project's MIT licence, but two —
PDFium and Skia — are BSD-3-Clause, whose terms require that their copyright notice and
disclaimer be reproduced in documentation distributed with a binary. That is what this
file is for.

| Component | Used for | Licence |
|---|---|---|
| [PdfPig](https://github.com/UglyToad/PdfPig) | Reading text and word positions out of a PDF | Apache-2.0 |
| [PDFsharp](https://github.com/empira/PDFsharp) | Writing the output document | MIT |
| [PDFtoImage](https://github.com/sungaila/PDFtoImage) | Managed wrapper around PDFium | MIT |
| [bblanchon/pdfium-binaries](https://github.com/bblanchon/pdfium-binaries) | Prebuilt PDFium binaries | Apache-2.0 (packaging) |
| [PDFium](https://pdfium.googlesource.com/pdfium/) | Rendering pages to pixels | BSD-3-Clause |
| [SkiaSharp](https://github.com/mono/SkiaSharp) | Managed wrapper around Skia | MIT |
| [Skia](https://skia.org/) | 2D drawing — every mark is painted with it | BSD-3-Clause |

The .NET runtime and WPF are bundled by the self-contained publish and are licensed
under the MIT License, © Microsoft Corporation.

Optical character recognition uses `Windows.Media.Ocr`, which is part of Windows itself
and is not redistributed by this project.

---

## BSD-3-Clause components (PDFium, Skia)

Copyright 2014 The PDFium Authors. All rights reserved.
Copyright 2011 Google Inc. All rights reserved.

Redistribution and use in source and binary forms, with or without modification, are
permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this list of
   conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice, this list
   of conditions and the following disclaimer in the documentation and/or other
   materials provided with the distribution.
3. Neither the name of Google Inc. nor the names of its contributors may be used to
   endorse or promote products derived from this software without specific prior
   written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY
EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF
MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL
THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT,
STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF
THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

---

## Apache-2.0 components (PdfPig, pdfium-binaries packaging)

Licensed under the Apache License, Version 2.0. You may obtain a copy of the licence at
<http://www.apache.org/licenses/LICENSE-2.0>. Distributed on an "AS IS" BASIS, WITHOUT
WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.

This project uses these components unmodified, as published packages.

---

*Licence summaries here are taken from each package's own metadata. They are a starting
point for your own review, not legal advice.*
