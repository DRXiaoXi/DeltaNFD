#pragma once
#include <algorithm>
#include <cstdint>
#include <limits>
#include <stdexcept>

namespace pacing {
enum class WaitStatus { completed, cancelled, timeout, failed };
struct Clock {
    virtual ~Clock() = default;
    virtual int64_t now() const = 0;
    virtual int64_t frequency() const = 0;
    virtual WaitStatus wait_until(int64_t deadline) = 0;
};
struct FrameResult {
    int64_t deadline = 0;
    int64_t ready = 0;
    int64_t submitted = 0; // CPU submission point, NEVER a physical display timestamp.
    int64_t wait_ticks = 0;
    int64_t late_ticks = 0;
    uint64_t discarded_slots = 0;
    bool rebased = false;
    WaitStatus status = WaitStatus::completed;
};

// Pure deadline scheduler. It only paces its CALLER; it cannot pace another process.
// Rational slot arithmetic keeps fractional frame periods from accumulating rounding error.
class FramePacer {
    Clock& clock_;
    int fps_;
    int64_t epoch_;
    int64_t slot_ = 1;
    int64_t last_submit_ = 0;
    int64_t tolerance_;
    uint64_t rebases_ = 0;
    uint64_t discarded_ = 0;
public:
    FramePacer(Clock& clock, int fps) : clock_(clock), fps_(fps), epoch_(clock.now()) {
        if (fps < 30 || fps > 1000 || clock.frequency() < 1000000)
            throw std::invalid_argument("FPS must be 30..1000 and clock >= 1 MHz");
        tolerance_ = std::max<int64_t>(1, clock.frequency() / 5000); // 200 us
    }
    int64_t deadline() const {
        // One run is bounded to 24h. No multiplication overflow on supported QPC frequencies.
        if (slot_ > std::numeric_limits<int64_t>::max() / clock_.frequency())
            throw std::overflow_error("deadline slot overflow");
        auto offset = slot_ * clock_.frequency() / fps_;
        if (epoch_ > std::numeric_limits<int64_t>::max() - offset)
            throw std::overflow_error("deadline overflow");
        return epoch_ + offset;
    }
    FrameResult pace() {
        FrameResult r;
        r.ready = clock_.now();
        r.deadline = deadline();
        if (r.ready < epoch_ || (last_submit_ && r.ready < last_submit_))
            throw std::runtime_error("clock moved backwards");
        r.status = clock_.wait_until(r.deadline);
        r.submitted = clock_.now();
        r.wait_ticks = std::max<int64_t>(0, r.submitted - r.ready);
        if (r.status != WaitStatus::completed) return r; // Failed waits NEVER publish a completed frame.
        if (r.submitted < r.deadline) throw std::runtime_error("wait completed before deadline");
        r.late_ticks = r.submitted - r.deadline;
        if (r.late_ticks > tolerance_) {
            // A long render or oversleep does NOT trigger multiple catch-up presents.
            // Discard the old schedule and place the NEXT frame a full period after actual submission.
            r.discarded_slots = static_cast<uint64_t>((r.late_ticks / clock_.frequency()) * fps_
                + (r.late_ticks % clock_.frequency()) * fps_ / clock_.frequency());
            epoch_ = r.submitted;
            slot_ = 1;
            r.rebased = true;
            ++rebases_;
            discarded_ += r.discarded_slots;
        } else {
            ++slot_;
        }
        last_submit_ = r.submitted;
        return r;
    }
    uint64_t rebases() const { return rebases_; }
    uint64_t discarded_slots() const { return discarded_; }
};

// Bounded EWMA of timer wake lateness, separate from render/present lateness.
class WakeEstimator {
    int64_t frequency_;
    int64_t estimate_ = 0;
public:
    explicit WakeEstimator(int64_t frequency) : frequency_(frequency) {}
    void observe(int64_t intended_wake, int64_t actual_wake) {
        auto sample = std::clamp<int64_t>(actual_wake - intended_wake, 0, frequency_ / 500); // <=2ms
        estimate_ += (sample - estimate_) / 8;
    }
    int64_t estimate() const { return estimate_; }
};
}
