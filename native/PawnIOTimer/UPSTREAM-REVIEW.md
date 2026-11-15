# Draft: bounded timer-resolution request module

This is a local review draft, not a submission or a claim of acceptance.

## Problem and behavior

Applications evaluating a kernel-held numerical timer request need a narrow
interface with explicit request ownership and cleanup. This proposal provides
set/query/release for 0.5..1ms, intersected with live platform limits. It reports
requested, granted-at-acquire and current intervals separately, including exact,
finer and coarser readback. It makes no latency, game-performance or Sleep claim.

The public interface exposes neither general kernel calls nor memory operations.
No request is made at module load. Same-value operations are idempotent; one VM
instance owns at most one reference. Normal explicit release/unload balances it.
The module is LGPL-2.1-or-later and every public IOCTL has Doxygen documentation.

## Verification

The actual compiled 64-bit AMX is run by pinned PawnPP in a user-mode harness
with fake timer natives. The harness checks request balance and ownership,
strict buffers/protocol, limits, finer/coarser observations, shared-buffer aliasing
and failures before or after a simulated kernel side effect. It never loads a
driver or changes the developer's real timer resolution.

## Questions requiring maintainer input

1. Is a bounded timer-resolution module within PawnIO's intended scope? The
   contribution guidance primarily describes replacing WinRing0-like access.
   This proposal's Windows APIs are documented, rather than hardware registers.
2. Can `data_v2p` be relied on for the supported official driver versions, and
   should its prototype be added to the Modules header first?
3. Normal `unload` is usable, but `vm_destroy` ignores its result and can skip it
   when pre-call checks fail. Is an upstream resource-owned timer reference
   needed to guarantee balancing independently of AMX cleanup execution?
4. A native exception has an ambiguous side effect. The module currently freezes
   mutations and reports uncertainty, avoiding guessed counter decrements. What
   failure policy/resource wrapper is acceptable for official signing?
5. What real-kernel/Driver Verifier/OS test matrix is required before acceptance?

Actual kernel validation and official module signing are outstanding. No signed
binary is supplied, and no installation changes or signature-policy changes are
requested by this review draft.
