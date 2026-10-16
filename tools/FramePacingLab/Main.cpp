#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <wrl/client.h>
#include <cmath>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <memory>
#include <random>
#include <string>
#include <vector>
#include "FramePacer.h"
#include "WinClock.h"

using Microsoft::WRL::ComPtr;
int run_core_tests();
namespace {
HANDLE cancel_event = nullptr;
BOOL WINAPI console_handler(DWORD event) {
    if (event == CTRL_C_EVENT || event == CTRL_BREAK_EVENT || event == CTRL_CLOSE_EVENT) {
        if (cancel_event) SetEvent(cancel_event);
        return TRUE;
    }
    return FALSE;
}
void hr(HRESULT value, const char* operation) {
    if (FAILED(value)) throw std::runtime_error(std::string(operation) + " failed HRESULT=" + std::to_string(value));
}
LRESULT CALLBACK window_proc(HWND hwnd, UINT message, WPARAM w, LPARAM l) {
    if (message == WM_CLOSE) { SetEvent(cancel_event); return 0; }
    return DefWindowProcW(hwnd, message, w, l);
}
struct Options {
    bool simulate = false, hidden = false, warp = false;
    int fps = 144, seconds = 5, frames = 8640;
    std::string scenario = "steady", mode = "absolute";
    std::filesystem::path output;
};
int integer(const wchar_t* text, int min, int max) {
    size_t used = 0;
    auto value = std::stoi(text, &used);
    if (used != wcslen(text) || value < min || value > max) throw std::invalid_argument("numeric argument out of range");
    return value;
}
std::string narrow(const std::wstring& value) {
    if (value.empty()) return {};
    auto length = WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    std::string result(length, '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), result.data(), length, nullptr, nullptr);
    return result;
}
Options parse(int argc, wchar_t** argv) {
    Options o;
    for (int i = 1; i < argc; ++i) {
        std::wstring arg = argv[i];
        if (arg == L"--simulate") o.simulate = true;
        else if (arg == L"--hidden") o.hidden = true;
        else if (arg == L"--warp") o.warp = true;
        else if (arg == L"--fps" && i + 1 < argc) o.fps = integer(argv[++i], 30, 1000);
        else if (arg == L"--seconds" && i + 1 < argc) o.seconds = integer(argv[++i], 1, 120);
        else if (arg == L"--frames" && i + 1 < argc) o.frames = integer(argv[++i], 2, 200000);
        else if (arg == L"--scenario" && i + 1 < argc) o.scenario = narrow(argv[++i]);
        else if (arg == L"--mode" && i + 1 < argc) o.mode = narrow(argv[++i]);
        else if (arg == L"--output" && i + 1 < argc) o.output = argv[++i];
        else throw std::invalid_argument("unknown or incomplete argument: " + narrow(arg));
    }
    if (o.output.empty()) throw std::invalid_argument("--output CSV is required");
    if (o.mode != "absolute" && o.mode != "relative" && o.mode != "none") throw std::invalid_argument("mode: absolute|relative|none");
    if (o.scenario != "steady" && o.scenario != "jitter" && o.scenario != "long-frame"
        && o.scenario != "timer-timeout" && o.scenario != "generated2x") throw std::invalid_argument("unknown scenario");
    if (!o.simulate && (o.scenario == "timer-timeout" || o.scenario == "generated2x"))
        throw std::invalid_argument("timeout and generated2x are simulation-only; this tool does not implement real DLSS/FG");
    if (o.scenario == "generated2x" && (o.fps % 2 || o.fps < 60))
        throw std::invalid_argument("synthetic 2x requires an even target FPS >= 60");
    if (std::filesystem::exists(o.output) || std::filesystem::exists(o.output.wstring() + L".json"))
        throw std::invalid_argument("output exists; choose a fresh file");
    if (o.output.has_parent_path()) std::filesystem::create_directories(o.output.parent_path());
    return o;
}
const char* status_text(pacing::WaitStatus status) {
    switch (status) {
    case pacing::WaitStatus::completed: return "completed";
    case pacing::WaitStatus::cancelled: return "cancelled";
    case pacing::WaitStatus::timeout: return "timeout";
    default: return "failed";
    }
}
struct Row {
    int frame;
    std::string kind;
    double start, ready, deadline, submit, interval, work, wait, late, queue_wait, present_call;
    uint64_t skipped;
    bool rebased;
    pacing::WaitStatus status;
    bool occluded = false;
};
void save(const Options& o, const std::vector<Row>& rows, const std::string& adapter, bool high_resolution,
    int exit_code, double cpu_ms, double wall_ms) {
    std::ofstream file(o.output, std::ios::binary);
    if (!file) throw std::runtime_error("cannot create CSV");
    file << "FrameIndex,FrameKind,RenderStartMs,RenderReadyMs,ScheduledDeadlineMs,PresentSubmitMs,PresentIntervalMs,RenderWorkMs,PacingWaitMs,LatenessMs,QueueWaitMs,PresentCallMs,DiscardedSlots,Rebased,WaitStatus,Occluded,RenderLateMs,WaitOvershootMs\n";
    file << std::fixed << std::setprecision(6);
    for (const auto& r : rows) file << r.frame << ',' << r.kind << ',' << r.start << ',' << r.ready << ','
        << r.deadline << ',' << r.submit << ',' << r.interval << ',' << r.work << ',' << r.wait << ','
        << r.late << ',' << r.queue_wait << ',' << r.present_call << ',' << r.skipped << ',' << r.rebased << ','
        << status_text(r.status) << ',' << r.occluded << ',' << std::max(0.0, r.ready - r.deadline) << ','
        << std::max(0.0, r.late - std::max(0.0, r.ready - r.deadline)) << '\n';
    file.close();
    if (!file) throw std::runtime_error("CSV write failed");
    std::ofstream meta(o.output.wstring() + L".json", std::ios::binary);
    // Adapter strings are supplied by DXGI; escape JSON metacharacters.
    std::string safe;
    for (char c : adapter) { if (c == '\\' || c == '"') safe += '\\'; if (static_cast<unsigned char>(c) >= 32) safe += c; }
    meta << "{\n\"schemaVersion\":1,\"source\":\"" << (o.simulate ? "synthetic" : "own-dx12-cpu-submission")
        << "\",\"mode\":\"" << o.mode << "\",\"scenario\":\"" << o.scenario << "\",\"targetDisplayedFps\":" << o.fps
        << ",\"simulatedGeneration\":" << (o.scenario == "generated2x" ? "true" : "false")
        << ",\"generationModel\":\"" << (o.scenario == "generated2x" ? "offline-ideal-midpoint-interpolation" : "none") << "\""
        << ",\"adapter\":\"" << safe << "\",\"hidden\":" << (o.hidden ? "true" : "false")
        << ",\"highResolutionTimer\":" << (high_resolution ? "true" : "false") << ",\"exitCode\":" << exit_code
        << ",\"processCpuMs\":" << cpu_ms << ",\"wallMs\":" << wall_ms
        << ",\"warning\":\"CPU submission/synthetic timestamps are not physical display or click-to-photon measurements.\"\n}\n";
    if (!meta) throw std::runtime_error("metadata write failed");
    std::cout << "Rows=" << rows.size() << " output=" << narrow(o.output.wstring()) << " source="
        << (o.simulate ? "synthetic (not game data)" : "own DX12 CPU submission (not display FPS)") << '\n';
}
double workload(const Options& o, std::mt19937& random, int frame) {
    if (o.scenario == "long-frame" && frame % 127 == 0) return 35.0;
    if (o.scenario == "jitter" || o.scenario == "long-frame") return 0.2 + (random() % 2500) / 1000.0;
    return 0.3;
}
struct SimClock final : pacing::Clock {
    int64_t tick = 0, overshoot = 0;
    bool timeout = false;
    int64_t now() const override { return tick; }
    int64_t frequency() const override { return 10000000; }
    pacing::WaitStatus wait_until(int64_t deadline) override {
        if (timeout) { tick += frequency() * 2; return pacing::WaitStatus::timeout; }
        if (tick < deadline) tick = deadline + overshoot;
        return pacing::WaitStatus::completed;
    }
};
pacing::FrameResult relative(pacing::Clock& clock, int fps) {
    pacing::FrameResult result;
    result.ready = clock.now(); result.deadline = result.ready + clock.frequency() / fps;
    result.status = clock.wait_until(result.deadline); result.submitted = clock.now();
    result.wait_ticks = result.submitted - result.ready;
    result.late_ticks = std::max<int64_t>(0, result.submitted - result.deadline);
    return result;
}
int simulate(const Options& o) {
    SimClock clock;
    int native_fps = o.scenario == "generated2x" ? o.fps / 2 : o.fps;
    pacing::FramePacer pacer(clock, native_fps);
    std::vector<Row> rows; rows.reserve(o.frames * (o.scenario == "generated2x" ? 2 : 1));
    std::mt19937 random(20261010);
    double previous = 0;
    int code = 0;
    for (int i = 1; i <= o.frames; ++i) {
        auto start = clock.now();
        clock.tick += static_cast<int64_t>(workload(o, random, i) * 10000);
        // Stable synthetic sleep overshoot is deliberately separated from work variability.
        clock.overshoot = o.scenario == "jitter" ? static_cast<int64_t>(random() % 1000) : 0;
        clock.timeout = o.scenario == "timer-timeout" && i == 50;
        auto result = o.mode == "absolute" ? pacer.pace() : o.mode == "relative" ? relative(clock, native_fps)
            : pacing::FrameResult{clock.now(), clock.now(), clock.now()};
        auto ms = [](int64_t ticks) { return ticks / 10000.0; };
        double submit = ms(result.submitted);
        double interval = previous ? submit - previous : 0;
        if (o.scenario == "generated2x" && previous && result.status == pacing::WaitStatus::completed) {
            // Offline ideal interpolation fixture, not a prediction of DLSS behavior.
            // Bracket with two known native frames; never fabricate an out-of-order display stream.
            auto generated = (previous + submit) * .5;
            rows.push_back({ i - 1, "synthetic_generated", 0, 0, generated, generated,
                interval * .5, 0, 0, 0, 0, 0, 0, false, pacing::WaitStatus::completed });
            interval *= .5;
        }
        rows.push_back({ i, "synthetic_native", ms(start), ms(result.ready), ms(result.deadline), submit,
            interval, ms(result.ready - start), ms(result.wait_ticks), ms(result.late_ticks),
            0, 0, result.discarded_slots, result.rebased, result.status });
        if (result.status != pacing::WaitStatus::completed) { code = 2; break; }
        previous = submit;
    }
    save(o, rows, "deterministic fake clock", false, code, 0, clock.now() / 10000.0);
    return code;
}

class Dx12Window {
    HWND window_ = nullptr;
    HANDLE fence_event_ = nullptr;
    HANDLE latency_ = nullptr;
    uint64_t fence_value_ = 0;
    ComPtr<IDXGIFactory6> factory_;
    ComPtr<ID3D12Device> device_;
    ComPtr<ID3D12CommandQueue> queue_;
    ComPtr<IDXGISwapChain3> swapchain_;
    ComPtr<ID3D12DescriptorHeap> rtv_;
    ComPtr<ID3D12Resource> targets_[2];
    ComPtr<ID3D12CommandAllocator> allocator_;
    ComPtr<ID3D12GraphicsCommandList> commands_;
    ComPtr<ID3D12Fence> fence_;
    UINT stride_ = 0;
    bool tearing_ = false;
public:
    std::string adapter_name;
    explicit Dx12Window(const Options& o) {
        WNDCLASSW wc{}; wc.lpfnWndProc = window_proc; wc.hInstance = GetModuleHandleW(nullptr); wc.lpszClassName = L"DeltaNFD.FramePacingLab";
        if (!RegisterClassW(&wc) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) throw std::runtime_error("RegisterClass failed");
        window_ = CreateWindowW(wc.lpszClassName, L"Delta NFD - own DX12 frame pacing research (not Delta Force)",
            WS_OVERLAPPEDWINDOW, CW_USEDEFAULT, CW_USEDEFAULT, 1280, 720, nullptr, nullptr, wc.hInstance, nullptr);
        if (!window_) throw std::runtime_error("CreateWindow failed");
        hr(CreateDXGIFactory2(0, IID_PPV_ARGS(&factory_)), "CreateDXGIFactory2");
        ComPtr<IDXGIAdapter1> adapter;
        if (o.warp) hr(factory_->EnumWarpAdapter(IID_PPV_ARGS(&adapter)), "EnumWarpAdapter");
        else {
            for (UINT i = 0;; ++i) {
                ComPtr<IDXGIAdapter1> candidate;
                auto status = factory_->EnumAdapterByGpuPreference(i, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE, IID_PPV_ARGS(&candidate));
                if (status == DXGI_ERROR_NOT_FOUND) break;
                hr(status, "EnumAdapterByGpuPreference");
                DXGI_ADAPTER_DESC1 desc{}; candidate->GetDesc1(&desc);
                if (!(desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE)
                    && SUCCEEDED(D3D12CreateDevice(candidate.Get(), D3D_FEATURE_LEVEL_11_0, __uuidof(ID3D12Device), nullptr))) { adapter = candidate; break; }
            }
            if (!adapter) throw std::runtime_error("no DX12 hardware adapter; use explicit --warp for software testing");
        }
        DXGI_ADAPTER_DESC1 desc{}; adapter->GetDesc1(&desc); adapter_name = narrow(desc.Description);
        hr(D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&device_)), "D3D12CreateDevice");
        D3D12_COMMAND_QUEUE_DESC queue_desc{}; queue_desc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        hr(device_->CreateCommandQueue(&queue_desc, IID_PPV_ARGS(&queue_)), "CreateCommandQueue");
        BOOL allow_tearing = FALSE;
        tearing_ = SUCCEEDED(factory_->CheckFeatureSupport(DXGI_FEATURE_PRESENT_ALLOW_TEARING, &allow_tearing, sizeof(allow_tearing))) && allow_tearing;
        DXGI_SWAP_CHAIN_DESC1 swap_desc{};
        swap_desc.Width = 1280; swap_desc.Height = 720; swap_desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        swap_desc.SampleDesc.Count = 1; swap_desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT; swap_desc.BufferCount = 2;
        swap_desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        swap_desc.Flags = DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT | (tearing_ ? DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING : 0);
        ComPtr<IDXGISwapChain1> swap;
        hr(factory_->CreateSwapChainForHwnd(queue_.Get(), window_, &swap_desc, nullptr, nullptr, &swap), "CreateSwapChainForHwnd");
        hr(factory_->MakeWindowAssociation(window_, DXGI_MWA_NO_ALT_ENTER), "MakeWindowAssociation");
        hr(swap.As(&swapchain_), "swapchain query");
        hr(swapchain_->SetMaximumFrameLatency(1), "SetMaximumFrameLatency");
        latency_ = swapchain_->GetFrameLatencyWaitableObject();
        if (!latency_) throw std::runtime_error("latency waitable object missing");
        D3D12_DESCRIPTOR_HEAP_DESC heap{}; heap.NumDescriptors = 2; heap.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
        hr(device_->CreateDescriptorHeap(&heap, IID_PPV_ARGS(&rtv_)), "CreateDescriptorHeap");
        stride_ = device_->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
        auto handle = rtv_->GetCPUDescriptorHandleForHeapStart();
        for (UINT i = 0; i < 2; ++i) {
            hr(swapchain_->GetBuffer(i, IID_PPV_ARGS(&targets_[i])), "GetBuffer");
            device_->CreateRenderTargetView(targets_[i].Get(), nullptr, handle); handle.ptr += stride_;
        }
        hr(device_->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&allocator_)), "CreateCommandAllocator");
        hr(device_->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocator_.Get(), nullptr, IID_PPV_ARGS(&commands_)), "CreateCommandList");
        hr(commands_->Close(), "initial Close");
        hr(device_->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence_)), "CreateFence");
        fence_event_ = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (!fence_event_) throw std::runtime_error("CreateEvent failed");
        if (!o.hidden) ShowWindow(window_, SW_SHOW);
    }
    ~Dx12Window() {
        if (fence_event_) CloseHandle(fence_event_);
        if (latency_) CloseHandle(latency_);
        if (window_) DestroyWindow(window_);
    }
    bool messages() {
        MSG msg{};
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&msg); DispatchMessageW(&msg); }
        return WaitForSingleObject(cancel_event, 0) != WAIT_OBJECT_0;
    }
    void queue_ready() {
        HANDLE handles[] = {cancel_event, latency_};
        auto result = WaitForMultipleObjects(2, handles, FALSE, 2000);
        if (result == WAIT_OBJECT_0) throw std::runtime_error("cancelled");
        if (result != WAIT_OBJECT_0 + 1) throw std::runtime_error("DXGI queue wait timed out/failed");
    }
    void render(int frame) {
        auto index = swapchain_->GetCurrentBackBufferIndex();
        hr(allocator_->Reset(), "allocator Reset");
        hr(commands_->Reset(allocator_.Get(), nullptr), "commands Reset");
        D3D12_RESOURCE_BARRIER barrier{};
        barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        barrier.Transition.pResource = targets_[index].Get(); barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_PRESENT; barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_RENDER_TARGET;
        commands_->ResourceBarrier(1, &barrier);
        auto handle = rtv_->GetCPUDescriptorHandleForHeapStart(); handle.ptr += static_cast<SIZE_T>(index) * stride_;
        float base[] = {0.03f, 0.04f, 0.08f, 1}; commands_->ClearRenderTargetView(handle, base, 0, nullptr);
        const LONG x = (frame * 7) % 1150;
        D3D12_RECT rect{x, 260, x + 120, 460};
        float moving[] = {0.2f, 0.65f, static_cast<float>(0.5 + 0.4 * std::sin(frame * 0.03)), 1};
        commands_->ClearRenderTargetView(handle, moving, 1, &rect);
        std::swap(barrier.Transition.StateBefore, barrier.Transition.StateAfter); commands_->ResourceBarrier(1, &barrier);
        hr(commands_->Close(), "commands Close"); ID3D12CommandList* lists[] = {commands_.Get()}; queue_->ExecuteCommandLists(1, lists);
        hr(queue_->Signal(fence_.Get(), ++fence_value_), "queue Signal");
        hr(fence_->SetEventOnCompletion(fence_value_, fence_event_), "SetEventOnCompletion");
        HANDLE handles[] = {cancel_event, fence_event_};
        auto result = WaitForMultipleObjects(2, handles, FALSE, 2000);
        if (result != WAIT_OBJECT_0 + 1) throw std::runtime_error("GPU fence cancelled/timed out/failed");
    }
    bool present() {
        auto result = swapchain_->Present(0, tearing_ ? DXGI_PRESENT_ALLOW_TEARING : 0);
        if (result == DXGI_STATUS_OCCLUDED) return true;
        hr(result, "Present"); return false;
    }
};
uint64_t filetime(FILETIME t) { return (static_cast<uint64_t>(t.dwHighDateTime) << 32) | t.dwLowDateTime; }
double cpu_time_ms() {
    FILETIME created{}, exited{}, kernel{}, user{};
    if (!GetProcessTimes(GetCurrentProcess(), &created, &exited, &kernel, &user)) return 0;
    return (filetime(kernel) + filetime(user)) / 10000.0;
}
int dx12(const Options& o) {
    Dx12Window renderer(o);
    WinClock clock(cancel_event);
    const auto origin = clock.now();
    const auto cpu_start = cpu_time_ms();
    pacing::FramePacer pacer(clock, o.fps);
    auto ms = [&](int64_t tick) { return (tick - origin) * 1000.0 / clock.frequency(); };
    auto duration = [&](int64_t ticks) { return ticks * 1000.0 / clock.frequency(); };
    std::vector<Row> rows; rows.reserve(o.fps * o.seconds + 10);
    std::mt19937 random(20261010);
    double previous = 0;
    int code = 0;
    try {
        for (int i = 1; clock.now() - origin < static_cast<int64_t>(o.seconds) * clock.frequency() && i < 200000; ++i) {
            if (!renderer.messages()) { code = 2; break; }
            auto queue_start = clock.now(); renderer.queue_ready(); auto start = clock.now();
            // Controlled artificial workload is in THIS test process, not in the game.
            auto cost = static_cast<int64_t>(workload(o, random, i) * clock.frequency() / 1000.0);
            if (clock.wait_until(clock.now() + cost) != pacing::WaitStatus::completed) { code = 2; break; }
            renderer.render(i);
            auto result = o.mode == "absolute" ? pacer.pace() : o.mode == "relative" ? relative(clock, o.fps)
                : pacing::FrameResult{clock.now(), clock.now(), clock.now()};
            if (result.status != pacing::WaitStatus::completed) { code = 2; break; }
            const auto submitted = clock.now();
            bool occluded = renderer.present(); const auto returned = clock.now();
            double submit = ms(submitted);
            rows.push_back({i, "dx12_cpu_submit", ms(start), ms(result.ready), ms(result.deadline), submit,
                previous ? submit - previous : 0, duration(result.ready - start), duration(result.wait_ticks),
                duration(result.late_ticks), duration(start - queue_start), duration(returned - submitted),
                result.discarded_slots, result.rebased, result.status, occluded});
            previous = submit;
            if (o.hidden && occluded) {
                // Hidden windows cannot prove scanout. Still exercise GPU rendering, with bounded polling.
                Sleep(1);
            }
        }
    } catch (const std::exception& e) { std::cerr << e.what() << '\n'; code = 2; }
    save(o, rows, renderer.adapter_name, clock.high_resolution(), code, cpu_time_ms() - cpu_start, duration(clock.now() - origin));
    return code;
}
}
int wmain(int argc, wchar_t** argv) {
    try {
        if (argc == 2 && std::wstring(argv[1]) == L"--self-test") return run_core_tests();
        if (argc == 2 && std::wstring(argv[1]) == L"--clock-test") {
            HANDLE event = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            if (!event) throw std::runtime_error("clock test event failed");
            {
                WinClock clock(event); auto deadline = clock.now() + clock.frequency() / 100;
                if (clock.wait_until(deadline) != pacing::WaitStatus::completed || clock.now() < deadline)
                    throw std::runtime_error("real timer deadline test failed");
                std::cout << "PASS: real timer never completes early\n";
                SetEvent(event);
                if (clock.wait_until(clock.now() + clock.frequency()) != pacing::WaitStatus::cancelled)
                    throw std::runtime_error("real timer cancellation test failed");
                std::cout << "PASS: real timer respects cancellation\n";
                ResetEvent(event);
                if (clock.wait_until(clock.now() - 1) != pacing::WaitStatus::completed)
                    throw std::runtime_error("expired timer test failed");
                std::cout << "PASS: expired deadline returns without catch-up wait\n";
            }
            CloseHandle(event); std::cout << "Windows clock checks: 3 passed\n"; return 0;
        }
        if (argc == 1 || (argc == 2 && std::wstring(argv[1]) == L"--help")) {
            std::cout << "FramePacingLab --self-test | --clock-test\n"
                "FramePacingLab [--simulate] [--fps 144] [--scenario steady|jitter|long-frame|timer-timeout|generated2x]\n"
                "  [--mode absolute|relative|none] [--seconds 5] [--frames 8640] [--warp] [--hidden] --output fresh.csv\n"
                "Own DX12 / synthetic only. Does not inject, configure, suspend, or cap Delta Force.\n";
            return 0;
        }
        auto options = parse(argc, argv);
        cancel_event = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!cancel_event) throw std::runtime_error("cancel event creation failed");
        SetConsoleCtrlHandler(console_handler, TRUE);
        auto result = options.simulate ? simulate(options) : dx12(options);
        SetConsoleCtrlHandler(console_handler, FALSE); CloseHandle(cancel_event); cancel_event = nullptr;
        return result;
    } catch (const std::exception& e) { std::cerr << e.what() << '\n'; return 1; }
}
