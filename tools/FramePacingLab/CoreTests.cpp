#include "FramePacer.h"
#include <cmath>
#include <iostream>
#include <string>
#include <vector>

struct FakeClock final : pacing::Clock {
    int64_t tick = 0;
    int64_t hz = 10000000;
    int64_t overshoot = 0;
    pacing::WaitStatus result = pacing::WaitStatus::completed;
    int64_t now() const override { return tick; }
    int64_t frequency() const override { return hz; }
    pacing::WaitStatus wait_until(int64_t deadline) override {
        if (result == pacing::WaitStatus::completed) tick = std::max(tick, deadline) + overshoot;
        return result;
    }
};

int run_core_tests() {
    int count = 0;
    auto check = [&](bool ok, const std::string& name) {
        if (!ok) throw std::runtime_error("FAIL: " + name);
        ++count; std::cout << "PASS: " << name << '\n';
    };
    for (auto fps : {30, 60, 72, 90, 120, 144, 165, 240, 360, 1000}) {
        FakeClock clock; pacing::FramePacer pacer(clock, fps);
        for (int frame = 1; frame <= fps * 60; ++frame) {
            clock.tick += clock.hz / 5000; // 200us render work
            auto result = pacer.pace();
            if (result.status != pacing::WaitStatus::completed || result.rebased
                || std::abs(result.submitted - static_cast<int64_t>(frame) * clock.hz / fps) > 1)
                throw std::runtime_error("rational schedule drift at " + std::to_string(fps));
        }
        check(clock.tick == clock.hz * 60, "60-second zero-drift " + std::to_string(fps) + " FPS");
    }
    FakeClock clock; pacing::FramePacer pacer(clock, 144);
    auto first = pacer.pace();
    clock.tick += clock.hz / 10; // 100 ms resource/shader stall
    auto late = pacer.pace(); auto after = pacer.pace();
    check(late.rebased && late.discarded_slots >= 13, "long render discards obsolete slots");
    check(after.submitted - late.submitted >= clock.hz / 144 - 1, "no catch-up burst after long frame");
    check(late.wait_ticks == 0 && late.late_ticks > 0, "render lateness is not timer overshoot");
    check(pacer.rebases() == 1, "one rebase for one long stall");
    FakeClock delayed; delayed.overshoot = delayed.hz / 1000;
    pacing::FramePacer delayed_pacer(delayed, 144);
    auto a = delayed_pacer.pace(); auto b = delayed_pacer.pace();
    check(a.rebased && b.submitted - a.submitted >= delayed.hz / 144, "late timer cannot trigger short catch-up frame");
    for (auto failure : {pacing::WaitStatus::timeout, pacing::WaitStatus::cancelled, pacing::WaitStatus::failed}) {
        FakeClock failed; failed.result = failure; pacing::FramePacer p(failed, 144);
        auto before = p.deadline(); auto result = p.pace();
        check(result.status == failure && p.deadline() == before && p.rebases() == 0,
            "unsuccessful wait never commits frame " + std::to_string(static_cast<int>(failure)));
    }
    for (auto fps : {0, 29, 1001, -1}) {
        bool rejected = false;
        try { FakeClock c; pacing::FramePacer invalid(c, fps); } catch (const std::invalid_argument&) { rejected = true; }
        check(rejected, "invalid FPS rejected " + std::to_string(fps));
    }
    FakeClock backwards; pacing::FramePacer monotonic(backwards, 144); monotonic.pace(); backwards.tick = 0;
    bool rejected = false;
    try { monotonic.pace(); } catch (const std::runtime_error&) { rejected = true; }
    check(rejected, "backwards clock rejected");
    pacing::WakeEstimator estimate(10000000);
    for (int i = 0; i < 100; ++i) estimate.observe(0, 10000000);
    check(estimate.estimate() <= 20000, "wake correction bounded to 2ms");
    estimate.observe(100, 0); check(estimate.estimate() >= 0, "early timer does not create negative correction");
    FakeClock generated; pacing::FramePacer native_pacer(generated, 72);
    std::vector<int64_t> display;
    for (int i = 0; i < 720; ++i) {
        auto r = native_pacer.pace(); display.push_back(r.submitted);
        display.push_back(r.submitted + generated.hz / 144);
    }
    for (size_t i = 1; i < display.size(); ++i)
        if (std::abs((display[i] - display[i - 1]) - generated.hz / 144) > 1)
            throw std::runtime_error("synthetic generated cadence uneven");
    check(display.size() == 1440, "synthetic 2x cadence separates native and displayed counts");
    FakeClock jitter; pacing::FramePacer random(jitter, 144);
    int64_t previous = 0;
    uint32_t seed = 7;
    for (int i = 0; i < 20000; ++i) {
        seed = seed * 1664525 + 1013904223;
        jitter.tick += (seed % 97 == 0) ? jitter.hz / 30 : (seed % 15000);
        auto r = random.pace();
        if (previous && r.submitted - previous < jitter.hz / 144 - 1) throw std::runtime_error("random catch-up burst");
        previous = r.submitted;
    }
    check(random.rebases() > 0, "random workload rebases without catch-up");
    std::cout << "Frame pacing core checks: " << count << " passed\n";
    return 0;
}
