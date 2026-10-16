#pragma once
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <intrin.h>
#include <system_error>
#include "FramePacer.h"

class WinClock final : public pacing::Clock {
    HANDLE timer_ = nullptr;
    HANDLE cancel_ = nullptr;
    int64_t frequency_ = 0;
    pacing::WakeEstimator estimator_;
    bool high_resolution_ = false;
    int64_t spin_ticks_ = 0;
    static int64_t query_frequency() {
        LARGE_INTEGER value{};
        if (!QueryPerformanceFrequency(&value)) throw std::runtime_error("QPC unavailable");
        return value.QuadPart;
    }
public:
    explicit WinClock(HANDLE cancel) : cancel_(cancel), frequency_(query_frequency()), estimator_(frequency_) {
        spin_ticks_ = frequency_ / 5000; // maximum 200us tail, no system timer/priority modification
        timer_ = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
            TIMER_MODIFY_STATE | SYNCHRONIZE);
        high_resolution_ = timer_ != nullptr;
        if (!timer_) timer_ = CreateWaitableTimerExW(nullptr, nullptr, 0, TIMER_MODIFY_STATE | SYNCHRONIZE);
        if (!timer_) throw std::system_error(GetLastError(), std::system_category(), "CreateWaitableTimerEx");
    }
    ~WinClock() override { if (timer_) CloseHandle(timer_); }
    int64_t frequency() const override { return frequency_; }
    int64_t now() const override { LARGE_INTEGER value{}; QueryPerformanceCounter(&value); return value.QuadPart; }
    bool high_resolution() const { return high_resolution_; }
    int64_t wake_estimate() const { return estimator_.estimate(); }
    pacing::WaitStatus wait_until(int64_t deadline) override {
        const auto started = now();
        for (;;) {
            if (WaitForSingleObject(cancel_, 0) == WAIT_OBJECT_0) return pacing::WaitStatus::cancelled;
            auto current = now();
            if (current >= deadline) return pacing::WaitStatus::completed;
            if (current - started > frequency_ * 2) return pacing::WaitStatus::timeout;
            auto remaining = deadline - current;
            auto sleep_ticks = remaining - spin_ticks_ - estimator_.estimate();
            if (sleep_ticks > 0) {
                LARGE_INTEGER due{};
                due.QuadPart = -std::max<int64_t>(1, static_cast<int64_t>(
                    static_cast<long double>(sleep_ticks) * 10000000.0L / frequency_));
                auto intended = current + sleep_ticks;
                if (!SetWaitableTimer(timer_, &due, 0, nullptr, nullptr, FALSE)) return pacing::WaitStatus::failed;
                HANDLE handles[] = { cancel_, timer_ };
                auto result = WaitForMultipleObjects(2, handles, FALSE, 2000);
                if (result == WAIT_OBJECT_0) return pacing::WaitStatus::cancelled;
                if (result == WAIT_TIMEOUT) return pacing::WaitStatus::timeout;
                if (result != WAIT_OBJECT_0 + 1) return pacing::WaitStatus::failed;
                estimator_.observe(intended, now());
            } else if (remaining > spin_ticks_) {
                // Estimate was large: yield via the timer, never busy-spin the entire estimate window.
                LARGE_INTEGER due{}; due.QuadPart = -1;
                if (!SetWaitableTimer(timer_, &due, 0, nullptr, nullptr, FALSE)) return pacing::WaitStatus::failed;
                HANDLE handles[] = { cancel_, timer_ };
                auto result = WaitForMultipleObjects(2, handles, FALSE, 2000);
                if (result == WAIT_OBJECT_0) return pacing::WaitStatus::cancelled;
                if (result == WAIT_TIMEOUT) return pacing::WaitStatus::timeout;
                if (result != WAIT_OBJECT_0 + 1) return pacing::WaitStatus::failed;
            } else {
                _mm_pause();
            }
        }
    }
};
