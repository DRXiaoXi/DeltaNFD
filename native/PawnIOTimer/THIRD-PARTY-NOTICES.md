# Third-party notices

## PawnIO module headers

The proposal is built against PawnIO.Modules include files at commit
24425a5299e84410751da882c121f322fb030b82. These headers carry 0BSD notices:

Copyright (C) 2025 namazso <admin@namazso.eu>

Permission to use, copy, modify, and/or distribute this software for any
purpose with or without fee is hereby granted.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES WITH
REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY
AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY SPECIAL, DIRECT,
INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM
LOSS OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR
OTHER TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE OR
PERFORMANCE OF THIS SOFTWARE.

The local `data_v2p` declaration corresponds to the same 0BSD native interface in
PawnIO's header at 9d52965895588b0ffa3703b72eec75ba19f4ccc0.

## PawnPP offline test dependency

The test harness uses unmodified PawnPP source at
8caadf22f4296a545d0c40346914616e252ffe4f. Its implementation files are MPL-2.0;
the source is available at https://github.com/namazso/PawnPP/tree/8caadf22f4296a545d0c40346914616e252ffe4f .
PawnPP source, compiler tools and the test EXE are not included in the proposal
archive. The harness source, dependency pins and commands are included.

## Pawn compiler build tool

Pawn 4.1.7152 is provided by CompuPhase under its included licenses/notices
(compiler sources Apache-2.0; other runtime components retain their notices).
It is a build-time dependency, not part of this module or its distribution.
https://github.com/compuphase/pawn/releases/tag/v4.1.7152

## Official PawnIO binaries

No PawnIO driver, installer, unrestricted edition, key or signature is included.
Official binary licensing differs from the source license. This module requires
upstream review/signing before use with the official driver. See:
https://github.com/namazso/PawnIO.Modules/wiki/Using-PawnIO-Modules
