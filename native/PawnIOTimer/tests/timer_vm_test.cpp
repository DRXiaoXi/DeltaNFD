// SPDX-License-Identifier: LGPL-2.1-or-later
// Runs the real AMX in PawnPP with fake timer natives. No Windows/kernel APIs.
#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <fstream>
#include <map>
#include <memory>
#include <stdexcept>
#include <string>
#include <vector>
#include "amx.h"
#include "amx_loader.h"

using VM = amx::amx<uint64_t, amx::memory_manager_harvard<amx::memory_backing_contignous_buffer, amx::memory_backing_paged_buffers<5>>>;
using Loader = amx::loader<VM>;
using Cell = VM::cell;
static constexpr Cell protocolMagic=0x504e4644, ok=0;
static Cell nt(uint32_t v) { return static_cast<Cell>(static_cast<int64_t>(static_cast<int32_t>(v))); }
static const Cell invalid=nt(0xc000000d), unsupported=nt(0xc00000bb), fault=nt(0xc0000005), badstate=nt(0xc0000184), dataerror=nt(0xc000003e);
static int checks=0;
static void check(bool b,const char* what) { if(!b) throw std::runtime_error(what); ++checks; }
struct Session;
struct World {
    Cell minimum=5000, maximum=156250, external=0, forcedCurrent=0, forcedGrant=UINT64_MAX;
    bool arch=true, missingSet=false, missingQuery=false, nullMapping=false;
    int queryCalls=0, sets=0, releases=0, failQueryAt=0, failAcquire=0, failRelease=0;
    std::map<Session*,Cell> owners;
    Cell current() const {
        if(forcedCurrent) return forcedCurrent;
        Cell c=external?external:maximum;
        for(auto [owner,value]:owners) { (void)owner; c=std::min(c,value); }
        return c;
    }
};
struct Session {
    World& world;
    Loader loader;
    std::vector<uint8_t> bytes;
    std::map<Cell,Cell*> privatePointers;
    Cell nextPointer=0x100000, initStatus=badstate;
    bool loaded=false, closed=false;
    explicit Session(World& w,const std::vector<uint8_t>& b):world(w),bytes(b) {
        Loader::callbacks_arg callbacks{natives,std::size(natives),nullptr,nullptr,this};
        auto error=loader.init(bytes.data(),bytes.size(),callbacks);
        check(error==amx::loader_error::success,"real AMX loader rejected module");
        auto vmerr=loader.amx.call(loader.get_main(),initStatus);
        check(vmerr==amx::error::success,"main failed in real VM");
        loaded=initStatus==ok;
    }
    ~Session() { if(loaded&&!closed) close(); }
    Cell close() {
        closed=true; Cell result=badstate;
        auto error=loader.amx.call(loader.get_public("unload"),result);
        check(error==amx::error::success,"unload failed in VM");
        return result;
    }
    struct Result { Cell status; std::vector<Cell> out; };
    Result call(const char* name,std::vector<Cell> input,size_t outCount=12,bool alias=false) {
        auto& vm=loader.amx;
        std::vector<Cell> output(std::max<size_t>(outCount,1),0xcccccccccccccccc);
        std::vector<Cell> combined(std::max<size_t>(outCount,input.size()+4)+1,0xcccccccccccccccc);
        if(alias) std::copy(input.begin(),input.end(),combined.begin()+4);
        Cell dummy{};
        Cell* ip=alias?combined.data()+4:(input.empty()?&dummy:input.data());
        Cell* op=alias?combined.data():output.data();
        Cell iva{},ova{};
        check(vm.mem.data().map(ip,std::max<size_t>(1,input.size()),iva),"map input");
        check(vm.mem.data().map(op,std::max<size_t>(1,outCount),ova),"map output");
        Cell result=badstate;
        auto error=vm.call(loader.get_public(name),result,{iva-vm.DAT,static_cast<Cell>(input.size()),ova-vm.DAT,static_cast<Cell>(outCount)});
        vm.mem.data().unmap(ova,std::max<size_t>(1,outCount));
        vm.mem.data().unmap(iva,std::max<size_t>(1,input.size()));
        check(error==amx::error::success,"public call failed in actual VM");
        if(alias) output.assign(combined.begin(),combined.begin()+outCount);
        else output.resize(outCount);
        return {result,std::move(output)};
    }
    Result set(Cell request) { return call("ioctl_timer_set",{protocolMagic,1,request}); }
    Result query() { return call("ioctl_timer_query",{protocolMagic,1}); }
    Result release() { return call("ioctl_timer_release",{protocolMagic,1}); }
    static Cell argument(VM* vm,Cell argv,size_t i) {
        auto p=vm->data_v2p(argv+static_cast<Cell>(i*sizeof(Cell)));
        if(!p) throw std::runtime_error("bad native argument"); return *p;
    }
    static amx::error arch(VM*,Loader*,void* user,Cell,Cell,Cell& result) {
        result=static_cast<Session*>(user)->world.arch?1:2; return amx::error::success;
    }
    static amx::error resolve(VM* vm,Loader*,void* user,Cell argc,Cell argv,Cell& result) {
        auto& w=static_cast<Session*>(user)->world;
        if(argc!=1) return amx::error::invalid_operand;
        std::string name;
        auto address=argument(vm,argv,0);
        for(size_t i=0;i<64;++i) { auto p=vm->data_v2p(address+i*sizeof(Cell)); if(!p) return amx::error::access_violation;
            if(!*p) break; name+=static_cast<char>(*p); }
        if(name=="ExSetTimerResolution") result=w.missingSet?0:1;
        else if(name=="ExQueryTimerResolution") result=w.missingQuery?0:2;
        else throw std::runtime_error("module resolved an unexpected kernel routine");
        return amx::error::success;
    }
    static amx::error mapPrivate(VM* vm,Loader*,void* user,Cell argc,Cell argv,Cell& result) {
        auto& s=*static_cast<Session*>(user);
        if(argc!=1) return amx::error::invalid_operand;
        auto p=vm->data_v2p(argument(vm,argv,0));
        if(!p || s.world.nullMapping) { result=0; return amx::error::success; }
        result=s.nextPointer++; s.privatePointers[result]=p;
        return amx::error::success;
    }
    static amx::error invoke(VM* vm,Loader*,void* user,Cell argc,Cell argv,Cell& result) {
        auto& s=*static_cast<Session*>(user); auto& w=s.world;
        if(argc!=18) return amx::error::invalid_operand;
        auto ret=vm->mem.data().translate(argument(vm,argv,1));
        if(!ret) return amx::error::access_violation;
        *ret=0; result=ok;
        Cell function=argument(vm,argv,0);
        if(function==2) {
            ++w.queryCalls;
            if(w.failQueryAt==w.queryCalls) { result=fault; return amx::error::success; }
            Cell values[]{w.maximum,w.minimum,w.current()};
            for(size_t i=0;i<3;++i) {
                Cell pointer=argument(vm,argv,i+2); auto it=s.privatePointers.find(pointer);
                if(it==s.privatePointers.end()) throw std::runtime_error("query used a pointer not from private data_v2p");
                if(*it->second!=0) throw std::runtime_error("query did not clear 64-bit destination before ULONG write");
                *it->second=values[i]&0xffffffff;
            }
        } else if(function==1) {
            Cell requested=argument(vm,argv,2), enable=argument(vm,argv,3);
            if(enable==1) {
                ++w.sets; int failure=w.failAcquire; w.failAcquire=0;
                if(failure==1) { result=fault; return amx::error::success; }
                if(w.owners.contains(&s)) throw std::runtime_error("duplicate acquired reference");
                if(requested<5000 || requested>10000) throw std::runtime_error("out-of-policy timer request");
                w.owners[&s]=requested;
                if(failure==2) { result=fault; return amx::error::success; }
                *ret=w.forcedGrant==UINT64_MAX?w.current():w.forcedGrant;
            } else if(enable==0 && requested==0) {
                ++w.releases; int failure=w.failRelease; w.failRelease=0;
                if(failure==1) { result=fault; return amx::error::success; }
                if(w.owners.erase(&s)!=1) throw std::runtime_error("release without this instance's known request");
                if(failure==2) result=fault;
                *ret=w.current();
            } else throw std::runtime_error("unexpected timer API arguments");
        } else throw std::runtime_error("module invoked an unapproved routine");
        return amx::error::success;
    }
    static constexpr Loader::native_arg natives[]={{"get_arch",arch},{"get_proc_address",resolve},{"data_v2p",mapPrivate},{"invoke",invoke}};
};

static std::vector<uint8_t> read(const char* path) {
    std::ifstream f(path,std::ios::binary); if(!f) throw std::runtime_error("cannot read AMX");
    return {std::istreambuf_iterator<char>(f),std::istreambuf_iterator<char>()};
}
int main(int argc,char** argv) {
  try {
    if(argc!=2) throw std::runtime_error("usage: timer_vm_test TimerResolution.amx");
    auto bytes=read(argv[1]);
    {
      World w; Session s(w,bytes); check(s.initStatus==ok&&w.sets==0&&w.releases==0,"load must be read-only");
      auto idle=s.query(); check(idle.status==ok&&idle.out[2]==0&&idle.out[10]==0,"idle query");
      for(Cell target:{5000ull,5067ull,7500ull,10000ull}) {
        auto a=s.set(target); check(a.status==ok&&a.out[3]==target&&a.out[4]==target&&a.out[5]==target&&a.out[10]==1,"exact target");
        int before=w.sets; check(s.set(target).status==ok&&w.sets==before,"same request must not reacquire");
      }
      w.external=5000; auto finer=s.query(); check(finer.status==ok&&finer.out[3]==10000&&finer.out[5]==5000&&finer.out[10]==2,"external finer request");
      w.external=0; w.forcedCurrent=156250; auto coarse=s.query(); check(coarse.status==ok&&coarse.out[10]==3,"report coarse readback honestly"); w.forcedCurrent=0;
      check(s.release().status==ok&&w.owners.empty(),"explicit release"); int old=w.releases;
      check(s.release().status==ok&&w.releases==old,"idempotent release"); check(s.close()==ok&&w.releases==old,"idle unload must not decrement");
    }
    {
      World w; Session s(w,bytes); check(s.set(5067).status==ok,"acquire before invalid buffers"); int sets=w.sets, releases=w.releases;
      for(Cell bad:{0ull,4999ull,10001ull,0xffffffffffffffffull,0x1000013cbull}) check(s.set(bad).status==invalid,"reject bad target without truncating");
      for(size_t n:{0u,1u,2u,4u}) { std::vector<Cell> in(n,0); if(n) in[0]=protocolMagic; check(s.call("ioctl_timer_set",in).status==invalid,"bad set input size"); }
      for(size_t n:{0u,1u,11u,13u}) {
        check(s.call("ioctl_timer_set",{protocolMagic,1,5000},n).status==invalid,"bad set output size");
        check(s.call("ioctl_timer_release",{protocolMagic,1},n).status==invalid,"bad release output size");
      }
      check(s.call("ioctl_timer_set",{protocolMagic+1,1,5000}).status==invalid,"bad magic");
      check(s.call("ioctl_timer_set",{protocolMagic,2,5000}).status==invalid,"bad protocol version");
      check(s.call("ioctl_timer_query",{protocolMagic,1,0}).status==invalid,"query rejects trailing cells");
      check(s.call("ioctl_timer_release",{protocolMagic}).status==invalid,"release missing version");
      check(w.sets==sets&&w.releases==releases&&w.owners.size()==1,"all malformed calls preserve existing ref");
      auto alias=s.call("ioctl_timer_set",{protocolMagic,1,7500},12,true); check(alias.status==ok&&alias.out[3]==7500,"METHOD_BUFFERED overlapping input/output");
      check(s.close()==ok&&w.owners.empty(),"connection-close release");
    }
    {
      World w; Session a(w,bytes),b(w,bytes); check(a.set(7500).status==ok&&b.set(5000).status==ok,"two independent owners");
      check(a.close()==ok&&w.owners.size()==1&&b.query().out[5]==5000,"close cannot release other owner's ref");
      check(b.close()==ok&&w.owners.empty(),"last owner cleanup");
    }
    for(int mode=0;mode<5;++mode) {
      World w; if(mode==0) w.arch=false; if(mode==1) w.missingSet=true; if(mode==2) w.missingQuery=true; if(mode==3) w.minimum=20000; if(mode==4) w.maximum=0;
      Session s(w,bytes); check(s.initStatus!=ok&&w.sets==0&&w.releases==0,"unsupported init must not mutate timers");
    }
    {
      World w; w.minimum=10000; Session s(w,bytes); check(s.set(5000).status==unsupported&&w.sets==0,"hardware clamp must not hide unsupported request"); check(s.set(10000).status==ok,"hardware 1ms floor");
    }
    {
      World w; Session s(w,bytes); check(s.set(5067).status==ok,"query-failure setup");
      w.failQueryAt=w.queryCalls+1; int releases=w.releases;
      check(s.set(7500).status==fault&&w.releases==releases&&w.owners[&s]==5067,"pre-change query failure preserves old ref");
      w.failQueryAt=w.queryCalls+2; check(s.set(7500).status==fault&&w.owners[&s]==7500,"post-acquire query failure still owns request");
      check(s.close()==ok&&w.owners.empty(),"post-acquire failure unload balances ref");
    }
    for(Cell value:{0ull,1ull,200000ull,0x100000000ull}) {
      World w; w.forcedGrant=value; Session s(w,bytes); check(s.set(5067).status==dataerror&&w.owners.size()==1,"invalid grant retains known cleanup responsibility");
      check(s.close()==ok&&w.owners.empty(),"invalid grant cleanup");
    }
    {
      World w; w.forcedGrant=0xdeadbeef000013cbull; Session s(w,bytes);
      auto result=s.set(5067); check(result.status==ok&&result.out[4]==5067,"ULONG return ignores undefined upper register bits");
      check(s.close()==ok&&w.owners.empty(),"ULONG return cleanup");
    }
    for(int after:{1,2}) {
      World w; Session s(w,bytes),other(w,bytes); check(other.set(10000).status==ok,"other owner before acquire fault");
      w.failAcquire=after; check(s.set(5067).status==fault,"surface faulting acquire"); auto q=s.query(); check(q.status==ok&&q.out[2]==2&&q.out[10]==4,"fault must remain explicitly unknown");
      int sets=w.sets,releases=w.releases; check(s.set(5000).status==badstate&&s.release().status==badstate,"fault forbids guessed retries");
      check(s.close()==badstate&&w.sets==sets&&w.releases==releases&&w.owners.contains(&other),"uncertain cleanup cannot decrement another owner's ref");
      check(other.close()==ok,"other owner remains independently releasable");
    }
    for(int after:{1,2}) {
      World w; Session s(w,bytes); check(s.set(5067).status==ok,"release-fault setup"); w.failRelease=after;
      check(s.release().status==fault&&s.query().out[2]==2,"surface uncertain release"); int count=w.releases;
      check(s.close()==badstate&&w.releases==count,"never blindly double-release after native fault");
    }
    std::printf("%d checks passed using actual AMX/PawnPP; kernel calls, driver loads and timer requests: 0\n",checks);
    return 0;
  } catch(const std::exception& e) { std::fprintf(stderr,"FAIL: %s\n",e.what()); return 1; }
}
