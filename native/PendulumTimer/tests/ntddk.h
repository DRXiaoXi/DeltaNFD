/* USER-MODE UNIT SHIM ONLY. Production build does not include this directory. */
#ifndef PN_UNIT_NTDDK
#define PN_UNIT_NTDDK
#include <stddef.h>
typedef long NTSTATUS;
typedef unsigned long ULONG;
typedef unsigned __int64 ULONG_PTR;
typedef unsigned char BOOLEAN;
typedef void VOID;
typedef void* PFILE_OBJECT;
typedef struct { unsigned short Length, MaximumLength; const wchar_t* Buffer; } UNICODE_STRING, *PUNICODE_STRING;
typedef struct { ULONG a; unsigned short b,c; unsigned char d[8]; } GUID;
typedef struct { ULONG marker; } FAST_MUTEX;
typedef struct { ULONG dwOSVersionInfoSize,dwMajorVersion,dwMinorVersion,dwBuildNumber; } RTL_OSVERSIONINFOW;
typedef struct _DEVICE_OBJECT { void* DeviceExtension; ULONG Flags; } DEVICE_OBJECT,*PDEVICE_OBJECT;
typedef struct { unsigned char MajorFunction; PFILE_OBJECT FileObject;
    struct { struct { ULONG IoControlCode, InputBufferLength, OutputBufferLength; } DeviceIoControl; } Parameters;
} IO_STACK_LOCATION,*PIO_STACK_LOCATION;
typedef struct { IO_STACK_LOCATION Stack; struct { void* SystemBuffer; } AssociatedIrp;
    struct { NTSTATUS Status; ULONG_PTR Information; } IoStatus; } IRP,*PIRP;
typedef struct _DRIVER_OBJECT DRIVER_OBJECT,*PDRIVER_OBJECT;
typedef NTSTATUS DRIVER_DISPATCH(PDEVICE_OBJECT,PIRP);
typedef VOID DRIVER_UNLOAD(PDRIVER_OBJECT);
typedef NTSTATUS DRIVER_INITIALIZE(PDRIVER_OBJECT,PUNICODE_STRING);
struct _DRIVER_OBJECT { PDEVICE_OBJECT DeviceObject; DRIVER_DISPATCH* MajorFunction[28]; DRIVER_UNLOAD* DriverUnload; };
#define TRUE 1
#define FALSE 0
#define STATUS_SUCCESS ((NTSTATUS)0)
#define STATUS_INVALID_DEVICE_REQUEST ((NTSTATUS)0xc0000010)
#define STATUS_INVALID_DEVICE_STATE ((NTSTATUS)0xc0000184)
#define STATUS_SHARING_VIOLATION ((NTSTATUS)0xc0000043)
#define STATUS_ACCESS_DENIED ((NTSTATUS)0xc0000022)
#define STATUS_INFO_LENGTH_MISMATCH ((NTSTATUS)0xc0000004)
#define STATUS_INVALID_PARAMETER ((NTSTATUS)0xc000000d)
#define STATUS_UNSUCCESSFUL ((NTSTATUS)0xc0000001)
#define STATUS_NOT_SUPPORTED ((NTSTATUS)0xc00000bb)
#define NT_SUCCESS(s) ((NTSTATUS)(s)>=0)
#define APC_LEVEL 1
#define IRP_MJ_CREATE 0
#define IRP_MJ_CLOSE 2
#define IRP_MJ_DEVICE_CONTROL 14
#define IRP_MJ_CLEANUP 18
#define IRP_MJ_MAXIMUM_FUNCTION 27
#define FILE_DEVICE_UNKNOWN 0x22
#define FILE_DEVICE_SECURE_OPEN 0x100
#define FILE_READ_DATA 1
#define FILE_WRITE_DATA 2
#define METHOD_BUFFERED 0
#define DO_DEVICE_INITIALIZING 0x80
#define IO_NO_INCREMENT 0
#define CTL_CODE(d,f,m,a) (((d)<<16)|((a)<<14)|((f)<<2)|(m))
#define C_ASSERT(c) typedef char compile_assert[(c)?1:-1]
#define RTL_CONSTANT_STRING(s) {0,0,s}
#define UNREFERENCED_PARAMETER(x) (void)(x)
#ifndef _Use_decl_annotations_
#define _Use_decl_annotations_
#endif
static UNICODE_STRING SDDL_DEVOBJ_SYS_ALL_ADM_ALL = RTL_CONSTANT_STRING(L"SYSTEM/ADMIN UNIT FIXTURE");
PIO_STACK_LOCATION IoGetCurrentIrpStackLocation(PIRP);
ULONG KeGetCurrentIrql(void);
void ExAcquireFastMutex(FAST_MUTEX*);
void ExReleaseFastMutex(FAST_MUTEX*);
void ExInitializeFastMutex(FAST_MUTEX*);
ULONG ExSetTimerResolution(ULONG,BOOLEAN);
void IoCompleteRequest(PIRP,int);
void RtlZeroMemory(void*,size_t);
NTSTATUS RtlGetVersion(RTL_OSVERSIONINFOW*);
NTSTATUS IoCreateDeviceSecure(PDRIVER_OBJECT,ULONG,PUNICODE_STRING,ULONG,ULONG,BOOLEAN,const UNICODE_STRING*,const GUID*,PDEVICE_OBJECT*);
NTSTATUS IoCreateSymbolicLink(PUNICODE_STRING,PUNICODE_STRING);
void IoDeleteSymbolicLink(PUNICODE_STRING);
void IoDeleteDevice(PDEVICE_OBJECT);
#endif
