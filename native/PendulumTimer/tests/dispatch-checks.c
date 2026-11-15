/* Compiles the actual driver source against fake kernel APIs. Never loads a driver. */
#include "../PendulumTimer.c"

__declspec(dllimport) VOID __stdcall ExitProcess(ULONG);
__declspec(dllimport) void* __stdcall GetStdHandle(ULONG);
__declspec(dllimport) int __stdcall WriteFile(void*,const void*,ULONG,ULONG*,void*);
static ULONG holds, releases, irql, build = 19045, checks;
static DEVICE_OBJECT device;
static PN_STATE extension;
static DRIVER_OBJECT driver;

PIO_STACK_LOCATION IoGetCurrentIrpStackLocation(PIRP p) { return &p->Stack; }
ULONG KeGetCurrentIrql(void) { return irql; }
void ExAcquireFastMutex(FAST_MUTEX* m) { (void)m; }
void ExReleaseFastMutex(FAST_MUTEX* m) { (void)m; }
void ExInitializeFastMutex(FAST_MUTEX* m) { m->marker=1; }
ULONG ExSetTimerResolution(ULONG value, BOOLEAN active) { if(active) { holds++; return value; } releases++; return 156250; }
void IoCompleteRequest(PIRP p,int boost) { (void)p; (void)boost; }
void RtlZeroMemory(void* p,size_t n) { volatile unsigned char* b=p; while(n--) *b++=0; }
NTSTATUS RtlGetVersion(RTL_OSVERSIONINFOW* v) { v->dwMajorVersion=10; v->dwBuildNumber=build; return 0; }
NTSTATUS IoCreateDeviceSecure(PDRIVER_OBJECT d,ULONG n,PUNICODE_STRING name,ULONG type,ULONG flags,BOOLEAN exclusive,const UNICODE_STRING* sddl,const GUID* guid,PDEVICE_OBJECT* out)
{ (void)n;(void)name;(void)type;(void)flags;(void)exclusive;(void)sddl;(void)guid; device.DeviceExtension=&extension; *out=&device; d->DeviceObject=&device; return 0; }
NTSTATUS IoCreateSymbolicLink(PUNICODE_STRING a,PUNICODE_STRING b) { (void)a;(void)b;return 0; }
void IoDeleteSymbolicLink(PUNICODE_STRING a) { (void)a; }
void IoDeleteDevice(PDEVICE_OBJECT d) { (void)d; }
static void Check(int yes) { if(!yes) ExitProcess(100+checks); checks++; }
static PN_PACKET Packet(ULONG value) { PN_PACKET p={PN_MAGIC,PN_VERSION,0,0,0,0};p.Requested100ns=value;return p; }
static NTSTATUS Call(unsigned char major,void* owner,ULONG code,PN_PACKET* packet,ULONG in,ULONG out)
{
    IRP irp={0}; irp.Stack.MajorFunction=major;irp.Stack.FileObject=owner;
    irp.Stack.Parameters.DeviceIoControl.IoControlCode=code;
    irp.Stack.Parameters.DeviceIoControl.InputBufferLength=in;
    irp.Stack.Parameters.DeviceIoControl.OutputBufferLength=out;
    irp.AssociatedIrp.SystemBuffer=packet;
    return PnDispatch(&device,&irp);
}
void mainCRTStartup(void)
{
    PN_PACKET packet; ULONG n,written; char text[100]="Driver dispatch fixture: "; char digits[10]; ULONG at=0,k=0;
    void* owner=(void*)1;void* other=(void*)2;
    Check(DriverEntry(&driver,NULL)==STATUS_SUCCESS);
    Check(holds==0 && releases==0); /* Loading alone does not request precision. */
    Check(Call(IRP_MJ_CREATE,owner,0,NULL,0,0)==0);
    Check(Call(IRP_MJ_CREATE,other,0,NULL,0,0)==STATUS_SHARING_VIOLATION);
    packet=Packet(5000);
    Check(Call(IRP_MJ_DEVICE_CONTROL,other,PN_SET,&packet,24,24)==STATUS_ACCESS_DENIED);
    Check(holds==0);
    for(n=0;n<24;n++) { packet=Packet(5000); Check(Call(IRP_MJ_DEVICE_CONTROL,owner,PN_SET,&packet,n,24)==STATUS_INFO_LENGTH_MISMATCH); }
    packet=Packet(5000);packet.Version=2;
    Check(Call(IRP_MJ_DEVICE_CONTROL,owner,PN_SET,&packet,24,24)==STATUS_INVALID_PARAMETER);
    packet=Packet(5000);packet.Reserved=1;
    Check(Call(IRP_MJ_DEVICE_CONTROL,owner,PN_SET,&packet,24,24)==STATUS_INVALID_PARAMETER);
    packet=Packet(5000);Check(Call(IRP_MJ_DEVICE_CONTROL,owner,PN_SET,&packet,24,23)==STATUS_INFO_LENGTH_MISMATCH);
    packet=Packet(5000);Check(Call(IRP_MJ_DEVICE_CONTROL,owner,PN_SET,&packet,25,24)==STATUS_INFO_LENGTH_MISMATCH);
    packet=Packet(4999);Check(Call(IRP_MJ_DEVICE_CONTROL,owner,PN_SET,&packet,24,24)==STATUS_INVALID_PARAMETER);
    packet=Packet(10001);Check(Call(IRP_MJ_DEVICE_CONTROL,owner,PN_SET,&packet,24,24)==STATUS_INVALID_PARAMETER);
    packet=Packet(5000);Check(Call(IRP_MJ_DEVICE_CONTROL,owner,PN_SET,&packet,24,24)==0);
    Check(packet.Active==1 && packet.Requested100ns==5000 && packet.Granted100ns==5000 && holds==1 && releases==0);
    packet=Packet(5000);Check(Call(IRP_MJ_DEVICE_CONTROL,owner,PN_SET,&packet,24,24)==0 && holds==1);
    packet=Packet(0);Check(Call(IRP_MJ_DEVICE_CONTROL,owner,PN_QUERY,&packet,24,24)==0 && holds==1 && packet.Active==1);
    packet=Packet(6000);Check(Call(IRP_MJ_DEVICE_CONTROL,owner,PN_SET,&packet,24,24)==0 && holds==2 && releases==1);
    packet=Packet(0);Check(Call(IRP_MJ_DEVICE_CONTROL,owner,PN_RELEASE,&packet,24,24)==0 && packet.Active==0 && releases==2);
    packet=Packet(0);Check(Call(IRP_MJ_DEVICE_CONTROL,owner,PN_RELEASE,&packet,24,24)==0 && releases==2);
    packet=Packet(5000);Check(Call(IRP_MJ_DEVICE_CONTROL,owner,PN_SET,&packet,24,24)==0);
    Check(Call(IRP_MJ_CLEANUP,other,0,NULL,0,0)==0 && extension.Active);
    Check(Call(IRP_MJ_CLEANUP,owner,0,NULL,0,0)==0 && !extension.Active && holds==releases);
    Check(Call(IRP_MJ_CLOSE,owner,0,NULL,0,0)==0 && holds==releases);
    Check(Call(IRP_MJ_CREATE,other,0,NULL,0,0)==0);
    irql=2;packet=Packet(5000);Check(Call(IRP_MJ_DEVICE_CONTROL,other,PN_SET,&packet,24,24)==STATUS_INVALID_DEVICE_STATE);irql=0;
    packet=Packet(5000);Check(Call(IRP_MJ_DEVICE_CONTROL,other,PN_SET,&packet,24,24)==0);
    Check(Call(22,other,0,NULL,0,0)==STATUS_INVALID_DEVICE_REQUEST && extension.Active);
    PnUnload(&driver);Check(holds==releases && !extension.Active);
    build=22000;Check(DriverEntry(&driver,NULL)==STATUS_NOT_SUPPORTED);
    while(text[at]) at++;
    n=checks; do { digits[k++]=(char)('0'+n%10); n/=10; } while(n);
    while(k) text[at++]=digits[--k];
    { const char* end=" checks passed; fake kernel APIs only.\r\n"; while(*end) text[at++]=*end++; }
    WriteFile(GetStdHandle((ULONG)-11),text,at,&written,NULL);
    ExitProcess(0);
}
