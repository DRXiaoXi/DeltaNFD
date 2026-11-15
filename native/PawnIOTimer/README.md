# Experimental PawnIO timer-resolution module

This is a local, unsigned module proposal. It is not accepted or signed by the
PawnIO maintainer and is not integrated into DeltaNFD. Official PawnIO builds
require maintainer-signed modules; this AMX cannot satisfy that requirement.
No bypass of module signatures is supplied.

The module is LGPL-2.1-or-later. The upstream include files retain their own
0BSD notices. See COPYING and THIRD-PARTY-NOTICES.md.

## Scope

The only public operations are `ioctl_timer_set`, `ioctl_timer_query`, and
`ioctl_timer_release`. Their arguments contain numbers, never addresses,
arbitrary routine names, process IDs, memory operations or callbacks.

Each loaded connection owns at most one request. Loading performs only capability
queries. Repeating the same request does not acquire another reference. A change
releases the old reference before acquiring the replacement. Explicit release
and normal connection close balance that instance's reference. Multiple
connections retain independent state; this is not an exclusive global lock.

The requested interval is restricted to **5000..10000 units of 100ns**
(0.5000..1.0000ms), intersected with the platform's advertised limits. Thus 5067
requests 0.5067ms. The returned interval can differ because of platform behavior
or other requests. Finer readback is reported separately from exact equality.
No Sleep measurement, busy loop, CPU load, timer/DPC callback, persistent setting,
driver installation or power-IRP handling is added.

The initial proposal supports x64 only and requires the two documented Windows
timer exports. It is intended for Win10 evaluation; actual hardware and OS
compatibility have not been established by the user-mode tests.

## Protocol

Use the supported PawnIOLib interface, with 64-bit cells. Counts below are **cell
counts**, not bytes. Every input must start with magic `0x504e4644`, version `1`.
All three operations require exactly **12 output cells**. Size and version
validation occurs before any timer mutation.

| Operation | Input cells | Meaning |
| --- | --- | --- |
| `ioctl_timer_set` | 3 | magic, version, requested interval |
| `ioctl_timer_query` | 2 | magic, version |
| `ioctl_timer_release` | 2 | magic, version |

Output layout:

| Index | Meaning |
| --- | --- |
| 0 | magic |
| 1 | protocol version |
| 2 | state: 0 idle, 1 known acquired reference, 2 mutation outcome uncertain |
| 3 | requested interval; an attempted target when state is uncertain |
| 4 | interval granted at the last successful acquire; not a live readback |
| 5 | live system interval at query time |
| 6 | platform minimum interval |
| 7 | platform maximum interval |
| 8 | allowed minimum: max(5000, platform minimum) |
| 9 | allowed maximum: min(10000, platform maximum) |
| 10 | relation: 0 idle, 1 exact, 2 finer, 3 coarser, 4 uncertain |
| 11 | last mutating native-call NTSTATUS, sign-extended to a 64-bit cell |

Clients must validate the complete returned protocol, state and intervals. A
successful RPC with state 1 does not prove exact numerical equality: inspect
relation and the actual values. RPC failure is not proof that nothing changed;
for example, readback can fail after a request was acquired. Always use explicit
release in a finally path, and then close the connection. Keep the connection
open only for the intended holding interval. Do not replay a timed-out mutation.

State 1 establishes ownership of a reference, not an independent promise of the
requested numerical precision. Reject a zero or out-of-hardware-range grant even
if a later query succeeds. A malformed grant does not remove the responsibility
to release the known reference.

## Implementation and lifecycle assumptions

The implementation resolves only constant names `ExSetTimerResolution` and
`ExQueryTimerResolution`. Three separate private one-cell buffers receive ULONG
query outputs. Entire 64-bit cells are cleared before each query; ULONG return
values are masked to 32 bits before validating them. `data_v2p` maps
these buffers individually; no assumption is made that adjacent AMX cells have
contiguous native backing. No native address is returned in the protocol.

Pinned PawnIO source serializes a VM's calls with a fast mutex, invokes module
`unload` on `IRP_MJ_CLOSE`, and rejects device-control calls outside PASSIVE_LEVEL.
The fast mutex raises execution to APC_LEVEL, which is within the documented
timer-API limit. The module registers no power callback and runs no timer call
from a power IRP.

`data_v2p` is exported in the pinned driver and declared in its native header,
but is absent in the pinned PawnIO.Modules header. This proposal declares that
prototype locally. Older driver builds lacking the native will reject the AMX
before main. Maintainer review must resolve the supported driver/header version.

**Fault boundary:** `invoke` can report a native exception without revealing
whether a mutation already took effect. In that case the module becomes state 2
and blocks further mutations, rather than guessing and potentially decrementing
another client's reference. An unresolved reference can remain in that failure
case. Also, PawnIO ignores `unload`'s return value, and a pre-call failure can
prevent the unload callback entirely. This module alone cannot guarantee cleanup
under those failures. A stronger upstream-owned resource/lifecycle mechanism may
be required before production acceptance. Normal cleanup and these limitations
are tested and reported separately.

## Build and offline verification

Pinned inputs:

* PawnIO: `9d52965895588b0ffa3703b72eec75ba19f4ccc0`
* PawnIO.Modules: `24425a5299e84410751da882c121f322fb030b82`
* PawnPP: `8caadf22f4296a545d0c40346914616e252ffe4f`
* Pawn compiler: 4.1.7152

Compile using upstream's module switches:

```text
pawncc TimerResolution.p -C64 -;+ -(+ -p -iPATH_TO_MODULES/include -oTimerResolution.amx
```

The local proposal archive includes the pinned, unmodified Modules headers at
`dependencies/PawnIO.Modules/include`; that directory can be used for the include
argument. It contains no PawnIO driver or compiler. Build tools and the pinned
PawnPP test dependency must be supplied separately.

`build_and_test.py` accepts explicit tool/include/library paths and compiles the
module plus `tests/timer_vm_test.cpp`. It runs the actual AMX in the pinned
PawnPP interpreter with fake kernel routines. The test executable makes
no Windows service, driver or timer API calls. It covers exact/custom requests,
hardware limits, other references, repeated changes, shared-buffer aliasing,
malformed arguments, normal close and injected pre/post-side-effect faults.

These checks do not establish real driver loading, x64 kernel ABI behavior,
actual process-death cleanup, game/ACE compatibility, Sleep behavior or long-term
stability. The product and original signed-driver experimental package remain
unchanged. There is no runtime installation/enable script in this proposal.

## Upstream review

See UPSTREAM-REVIEW.md for the proposed review description and open questions.
No pull request, issue or message has been sent. The official acceptance process
is described at:
https://github.com/namazso/PawnIO.Modules/wiki/Contribution-guidelines

API references:

* https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/wdm/nf-wdm-exsettimerresolution
* https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/wdm/nf-wdm-exquerytimerresolution
