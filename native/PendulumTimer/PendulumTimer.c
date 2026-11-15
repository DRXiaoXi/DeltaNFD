/* Self-owned Win10 control driver. No third-party driver code or binary is used. */
#include <ntddk.h>
#include <wdmsec.h>

#define PN_MAGIC 0x504e4644u
#define PN_VERSION 1u
#define PN_SET CTL_CODE(FILE_DEVICE_UNKNOWN, 0x901, METHOD_BUFFERED, FILE_READ_DATA | FILE_WRITE_DATA)
#define PN_QUERY CTL_CODE(FILE_DEVICE_UNKNOWN, 0x902, METHOD_BUFFERED, FILE_READ_DATA | FILE_WRITE_DATA)
#define PN_RELEASE CTL_CODE(FILE_DEVICE_UNKNOWN, 0x903, METHOD_BUFFERED, FILE_READ_DATA | FILE_WRITE_DATA)

typedef struct _PN_PACKET {
    ULONG Magic, Version, Requested100ns, Granted100ns, Active, Reserved;
} PN_PACKET;
C_ASSERT(sizeof(PN_PACKET) == 24);

typedef struct _PN_STATE {
    FAST_MUTEX Mutex;
    PFILE_OBJECT Owner;
    ULONG Requested, Granted;
    BOOLEAN Active;
} PN_STATE;

static const GUID PnClass = { 0x863cad83, 0x7d34, 0x482e, { 0xac, 0x69, 0x61, 0x71, 0xf8, 0x1a, 0x8d, 0xb7 } };
static UNICODE_STRING PnLink = RTL_CONSTANT_STRING(L"\\DosDevices\\DeltaNFDPendulumTimerV1");

DRIVER_INITIALIZE DriverEntry;
DRIVER_UNLOAD PnUnload;
DRIVER_DISPATCH PnDispatch;

static VOID PnReleaseLocked(PN_STATE* state)
{
    /* Must never be called synchronously from an IRP_MJ_POWER handler. */
    if (state->Active) {
        ExSetTimerResolution(0, FALSE);
        state->Active = FALSE;
        state->Requested = state->Granted = 0;
    }
}

_Use_decl_annotations_
NTSTATUS PnDispatch(PDEVICE_OBJECT device, PIRP irp)
{
    PIO_STACK_LOCATION stack = IoGetCurrentIrpStackLocation(irp);
    PN_STATE* state = (PN_STATE*)device->DeviceExtension;
    NTSTATUS status = STATUS_INVALID_DEVICE_REQUEST;
    ULONG_PTR returned = 0;

    if (KeGetCurrentIrql() > APC_LEVEL) {
        status = STATUS_INVALID_DEVICE_STATE;
        goto Complete;
    }
    ExAcquireFastMutex(&state->Mutex);
    switch (stack->MajorFunction) {
    case IRP_MJ_CREATE:
        if (state->Owner != NULL) { status = STATUS_SHARING_VIOLATION; break; }
        state->Owner = stack->FileObject;
        status = STATUS_SUCCESS;
        break;
    case IRP_MJ_CLEANUP:
    case IRP_MJ_CLOSE:
        if (state->Owner == stack->FileObject) {
            PnReleaseLocked(state);
            state->Owner = NULL;
        }
        status = STATUS_SUCCESS;
        break;
    case IRP_MJ_DEVICE_CONTROL:
    {
        ULONG code = stack->Parameters.DeviceIoControl.IoControlCode;
        PN_PACKET* packet = (PN_PACKET*)irp->AssociatedIrp.SystemBuffer;
        if (state->Owner != stack->FileObject) { status = STATUS_ACCESS_DENIED; break; }
        if (code != PN_SET && code != PN_QUERY && code != PN_RELEASE) break;
        if (stack->Parameters.DeviceIoControl.InputBufferLength != sizeof(PN_PACKET) ||
            stack->Parameters.DeviceIoControl.OutputBufferLength != sizeof(PN_PACKET) || packet == NULL) {
            status = STATUS_INFO_LENGTH_MISMATCH; break;
        }
        if (packet->Magic != PN_MAGIC || packet->Version != PN_VERSION ||
            packet->Granted100ns != 0 || packet->Active != 0 || packet->Reserved != 0) {
            status = STATUS_INVALID_PARAMETER; break;
        }
        if (code == PN_SET) {
            ULONG requested = packet->Requested100ns;
            /* Bounded timer-only interface: 0.5ms through 1ms, no user pointers. */
            if (requested < 5000 || requested > 10000) { status = STATUS_INVALID_PARAMETER; break; }
            if (!state->Active || state->Requested != requested) {
                PnReleaseLocked(state);
                state->Granted = ExSetTimerResolution(requested, TRUE);
                state->Requested = requested;
                state->Active = TRUE;
                if (state->Granted == 0) {
                    PnReleaseLocked(state);
                    status = STATUS_UNSUCCESSFUL; break;
                }
            }
        } else {
            if (packet->Requested100ns != 0) { status = STATUS_INVALID_PARAMETER; break; }
            if (code == PN_RELEASE) PnReleaseLocked(state);
        }
        RtlZeroMemory(packet, sizeof(*packet));
        packet->Magic = PN_MAGIC;
        packet->Version = PN_VERSION;
        packet->Requested100ns = state->Requested;
        packet->Granted100ns = state->Granted;
        packet->Active = state->Active ? 1 : 0;
        returned = sizeof(*packet);
        status = STATUS_SUCCESS;
        break;
    }
    default:
        break;
    }
    ExReleaseFastMutex(&state->Mutex);
Complete:
    irp->IoStatus.Status = status;
    irp->IoStatus.Information = returned;
    IoCompleteRequest(irp, IO_NO_INCREMENT);
    return status;
}

_Use_decl_annotations_
VOID PnUnload(PDRIVER_OBJECT driver)
{
    PDEVICE_OBJECT device = driver->DeviceObject;
    if (device != NULL) {
        PN_STATE* state = (PN_STATE*)device->DeviceExtension;
        ExAcquireFastMutex(&state->Mutex);
        PnReleaseLocked(state);
        ExReleaseFastMutex(&state->Mutex);
        IoDeleteSymbolicLink(&PnLink);
        IoDeleteDevice(device);
    }
}

_Use_decl_annotations_
NTSTATUS DriverEntry(PDRIVER_OBJECT driver, PUNICODE_STRING registryPath)
{
    UNICODE_STRING name = RTL_CONSTANT_STRING(L"\\Device\\DeltaNFDPendulumTimerV1");
    PDEVICE_OBJECT device = NULL;
    RTL_OSVERSIONINFOW version = { 0 };
    NTSTATUS status;
    ULONG i;
    UNREFERENCED_PARAMETER(registryPath);
    version.dwOSVersionInfoSize = sizeof(version);
    status = RtlGetVersion(&version);
    if (!NT_SUCCESS(status)) return status;
    if (version.dwMajorVersion != 10 || version.dwBuildNumber < 17763 || version.dwBuildNumber >= 22000)
        return STATUS_NOT_SUPPORTED;
    status = IoCreateDeviceSecure(driver, sizeof(PN_STATE), &name, FILE_DEVICE_UNKNOWN,
        FILE_DEVICE_SECURE_OPEN, TRUE, &SDDL_DEVOBJ_SYS_ALL_ADM_ALL, &PnClass, &device);
    if (!NT_SUCCESS(status)) return status;
    RtlZeroMemory(device->DeviceExtension, sizeof(PN_STATE));
    ExInitializeFastMutex(&((PN_STATE*)device->DeviceExtension)->Mutex);
    for (i = 0; i <= IRP_MJ_MAXIMUM_FUNCTION; ++i) driver->MajorFunction[i] = PnDispatch;
    driver->DriverUnload = PnUnload;
    status = IoCreateSymbolicLink(&PnLink, &name);
    if (!NT_SUCCESS(status)) { IoDeleteDevice(device); return status; }
    device->Flags &= ~DO_DEVICE_INITIALIZING;
    return STATUS_SUCCESS;
}
