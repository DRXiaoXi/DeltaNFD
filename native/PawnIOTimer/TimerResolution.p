// SPDX-License-Identifier: LGPL-2.1-or-later
// Copyright (C) 2026 DeltaNFD contributors
// Experimental PawnIO module. No driver or module signature is included.

#include <pawnio.inc>

// Exported by the pinned PawnIO driver; not yet declared in the Modules headers.
// This maps only our private single-cell arrays, never an IOCTL argument.
native VA:data_v2p(data[]);

const TIMER_MAGIC = 0x504e4644;
const TIMER_VERSION = 1;
const TIMER_MIN = 5000;
const TIMER_MAX = 10000;
const TIMER_OUTPUT_CELLS = 12;

new VAProc:g_set = VAProc:0;
new VAProc:g_query = VAProc:0;
new bool:g_ready = false;
new bool:g_active = false;
new bool:g_uncertain = false;
new g_requested = 0;
new g_granted = 0;
new g_current = 0;
new g_minimum = 0;
new g_maximum = 0;
new NTSTATUS:g_last_status = STATUS_SUCCESS;

// Separate one-cell buffers: data_v2p promises only the first mapped cell.
// ExQueryTimerResolution writes a ULONG, so clear each entire 64-bit cell first.
new g_query_maximum[1];
new g_query_minimum[1];
new g_query_current[1];

stock NTSTATUS:query_resolution() {
    g_query_maximum[0] = 0;
    g_query_minimum[0] = 0;
    g_query_current[0] = 0;
    new VA:pmax = data_v2p(g_query_maximum);
    new VA:pmin = data_v2p(g_query_minimum);
    new VA:pcur = data_v2p(g_query_current);
    if (pmax == NULL || pmin == NULL || pcur == NULL)
        return STATUS_INSUFFICIENT_RESOURCES;

    new ignored;
    new NTSTATUS:status = invoke(g_query, ignored, _:pmax, _:pmin, _:pcur);
    if (!NT_SUCCESS(status))
        return status;

    new minimum = g_query_minimum[0] & 0xffffffff;
    new maximum = g_query_maximum[0] & 0xffffffff;
    new current = g_query_current[0] & 0xffffffff;
    if (minimum <= 0 || maximum < minimum || current < minimum || current > maximum)
        return STATUS_DATA_ERROR;
    g_minimum = minimum;
    g_maximum = maximum;
    g_current = current;
    return STATUS_SUCCESS;
}

stock write_snapshot(out[TIMER_OUTPUT_CELLS]) {
    out[0] = TIMER_MAGIC;
    out[1] = TIMER_VERSION;
    out[2] = g_uncertain ? 2 : (g_active ? 1 : 0);
    out[3] = g_requested;
    out[4] = g_granted;
    out[5] = g_current;
    out[6] = g_minimum;
    out[7] = g_maximum;
    out[8] = max(TIMER_MIN, g_minimum);
    out[9] = min(TIMER_MAX, g_maximum);
    out[10] = g_uncertain ? 4 : (!g_active ? 0 : (g_current == g_requested ? 1 : (g_current < g_requested ? 2 : 3)));
    out[11] = _:g_last_status;
}

stock NTSTATUS:release_request() {
    // A faulting native call may have changed state before throwing. Do not
    // guess a reference count, retry a mutation, or release another client's ref.
    if (g_uncertain)
        return STATUS_INVALID_DEVICE_STATE;
    if (!g_active)
        return STATUS_SUCCESS;
    new ignored;
    new NTSTATUS:status = invoke(g_set, ignored, 0, 0);
    g_last_status = status;
    if (!NT_SUCCESS(status)) {
        g_uncertain = true;
        return status;
    }
    g_active = false;
    g_requested = 0;
    g_granted = 0;
    return STATUS_SUCCESS;
}

/// Acquire or change this connection's one timer request, in 100ns units.
/// @param in [magic, version, requested100ns], requested100ns within 5000..10000
///             and the hardware's current advertised limits.
/// @param in_size Must be exactly 3 cells.
/// @param out Twelve-cell snapshot documented in README.md. No addresses.
/// @param out_size Must be exactly 12 cells; validated before any side effect.
/// @return NTSTATUS. On a post-acquire readback failure a request can remain
///         active; query/release it or close the connection. Fault state is 2.
DEFINE_IOCTL_SIZED(ioctl_timer_set, 3, TIMER_OUTPUT_CELLS) {
    if (in[0] != TIMER_MAGIC || in[1] != TIMER_VERSION)
        return STATUS_INVALID_PARAMETER;
    new requested = in[2];
    if (requested < TIMER_MIN || requested > TIMER_MAX)
        return STATUS_INVALID_PARAMETER;
    if (!g_ready || g_uncertain)
        return STATUS_INVALID_DEVICE_STATE;

    new NTSTATUS:status = query_resolution();
    if (!NT_SUCCESS(status))
        return status;
    if (requested < g_minimum || requested > g_maximum)
        return STATUS_NOT_SUPPORTED;

    if (!g_active || requested != g_requested) {
        status = release_request();
        if (!NT_SUCCESS(status))
            return status;
        new granted;
        status = invoke(g_set, granted, requested, 1);
        g_last_status = status;
        g_requested = requested;
        if (!NT_SUCCESS(status)) {
            g_uncertain = true;
            return status;
        }
        g_active = true;
        // The routine returns ULONG. Upper return-register bits are not its ABI.
        g_granted = granted & 0xffffffff;
        if (g_granted < g_minimum || g_granted > g_maximum)
            return STATUS_DATA_ERROR;
    }
    status = query_resolution();
    if (!NT_SUCCESS(status))
        return status;
    write_snapshot(out);
    return STATUS_SUCCESS;
}

/// Read this connection's state and the live system resolution without setting it.
/// @param in [magic, version].
/// @param in_size Must be exactly 2 cells.
/// @param out Twelve-cell snapshot; state 2 reports a faulted mutation.
/// @param out_size Must be exactly 12 cells.
/// @return NTSTATUS; a failed query does not release an existing request.
DEFINE_IOCTL_SIZED(ioctl_timer_query, 2, TIMER_OUTPUT_CELLS) {
    if (in[0] != TIMER_MAGIC || in[1] != TIMER_VERSION)
        return STATUS_INVALID_PARAMETER;
    if (!g_ready)
        return STATUS_INVALID_DEVICE_STATE;
    new NTSTATUS:status = query_resolution();
    if (!NT_SUCCESS(status))
        return status;
    write_snapshot(out);
    return STATUS_SUCCESS;
}

/// Release only a request successfully acquired by this module instance.
/// @param in [magic, version].
/// @param in_size Must be exactly 2 cells.
/// @param out Twelve-cell snapshot after release and readback.
/// @param out_size Must be exactly 12 cells; validated before release.
/// @return NTSTATUS. Already idle is successful without another release call.
DEFINE_IOCTL_SIZED(ioctl_timer_release, 2, TIMER_OUTPUT_CELLS) {
    if (in[0] != TIMER_MAGIC || in[1] != TIMER_VERSION)
        return STATUS_INVALID_PARAMETER;
    if (!g_ready)
        return STATUS_INVALID_DEVICE_STATE;
    new NTSTATUS:status = release_request();
    if (!NT_SUCCESS(status))
        return status;
    status = query_resolution();
    if (!NT_SUCCESS(status))
        return status;
    write_snapshot(out);
    return STATUS_SUCCESS;
}

NTSTATUS:main() {
    if (get_arch() != ARCH_X64)
        return STATUS_NOT_SUPPORTED;
    g_set = get_proc_address(''ExSetTimerResolution'');
    g_query = get_proc_address(''ExQueryTimerResolution'');
    if (g_set == VAProc:0 || g_query == VAProc:0)
        return STATUS_NOT_SUPPORTED;
    new NTSTATUS:status = query_resolution();
    if (!NT_SUCCESS(status))
        return status;
    if (max(TIMER_MIN, g_minimum) > min(TIMER_MAX, g_maximum))
        return STATUS_NOT_SUPPORTED;
    // Load performs capability queries only; it never acquires a request.
    g_ready = true;
    return STATUS_SUCCESS;
}

/// Called by PawnIO on connection close. Normal cleanup balances our one ref.
/// Upstream ignores this return value; see README.md for fault/unload limitations.
public NTSTATUS:unload() {
    return release_request();
}
