# Third-party notices

Scene3D uses third-party material as described below.

## prc-rs

`Caly.Rendering.Scene3D.Prc` contains code ported from, and data taken from, **prc-rs**
(<https://github.com/ralovich/prc-rs>, commit `5595499` of 2026-10-09): the bit-level decoders of the PRC
format (`Caly.Rendering.Scene3D.Prc/Bits`), the table of the PRC double encoding (`PrcDoubleTable.g.cs`), the
description of the format's structures (`Caly.Rendering.Scene3D.Prc/Schema/prc.json`) from which the entity readers
(`Caly.Rendering.Scene3D.Prc/Generated/PrcReaders.g.cs`, `PrcEntities.g.cs`) are generated, the list of entity type
numbers (`PrcType.g.cs`), the order of evaluation of embedded schemas (`Schema/PrcSchemaEvaluator.cs`),
the generator itself in so far as it follows prc-rs's `src/generate_rust`
(`tools/Caly.Rendering.Scene3D.Prc.Generator`), and the test vectors `Caly.Rendering.Scene3D.Tests/TestData/Prc/read_ints.bin`
and `read_doubles.bin`.

prc-rs is licensed under the MIT License:

> Copyright (c) 2025,2026 Kristof Ralovich
>
> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
> associated documentation files (the "Software"), to deal in the Software without restriction, including
> without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
> copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the
> following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or substantial
> portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT
> LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO
> EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
> IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR
> THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## Universal 3D Sample Software

`Caly.Rendering.Scene3D.Tests/TestData/TestScenes/*.u3d` and `*.idtf` come from the "Samples/TestScenes" folder of
the Universal 3D Sample Software (<https://github.com/ningfei/u3d>), Copyright (c) Intel Corporation and
contributors, licensed under the Apache License, Version 2.0 (<http://www.apache.org/licenses/LICENSE-2.0>).
They are used unmodified as test inputs and are not part of the libraries.

The U3D reader is written from ECMA-363 (Universal 3D File Format, 4th edition). Where the standard's own
listing and the reference decoder of the Sample Software disagree, the reference decoder's observable
behaviour is followed.

The standard does not say how progressive mesh detail, line sets and point sets predict what they store,
nor in what order the members of the sets they index are taken. Their decoding
(`Caly.Rendering.Scene3D.U3D/Internal/U3DDocument.Progressive.cs`, `U3DDocument.Sets.cs`) and the numbers of the
compression contexts (`U3DContexts.cs`, which includes those of motion resources and bone weights) are derived from the Sample Software's `CIFXAuthorCLODDecoder_P.cpp`,
`CIFXSetAdjacencyX.cpp`, `CIFXSetX.cpp`, `CIFXLineSetDecoder.cpp`, `CIFXPointSetDecoder.cpp`,
`IFXQuaternion.cpp` and `IFXACContext.h`: the order of what is read, the sets and the predictions,
rewritten in C#.

The reading of motion resources, skeleton descriptions and bone weights (`U3DDocument.Animation.cs`, `U3DDocument.Meshes.cs`) follows `CIFXMotionDecoder.cpp`, the bone part of `CIFXAuthorCLODDecoder.cpp` and `CIFXBoneWeightsModifierDecoder.cpp`; the pose of a skeleton and the deformation of a skinned mesh (`SceneSkeleton.cs`, `SceneSkin.cs`) follow the arithmetic of `IFXCharacter.cpp` (`ForEachNodeTransformed2`), `IFXMotionMixerImpl.cpp`, `IFXBonesManagerImpl.cpp` and `IFXSkin.cpp`, rewritten in C#.
