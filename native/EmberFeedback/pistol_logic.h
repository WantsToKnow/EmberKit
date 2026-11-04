// Copyright 2026 EmberKit contributors. SPDX-License-Identifier: Apache-2.0
#pragma once
#include <cstdint>

namespace ember {
enum class MouseAction { none, down, up };

// Track the physical button separately: our injected releases must not stop the hold.
class PistolPulse {
    bool held_{}, active_{}, down_{};
    std::uint64_t next_{};
public:
    void Observe(bool injected, bool down) { if (!injected) held_ = down; }
    bool Active() const { return active_; }
    void Reset() { held_ = active_ = down_ = false; next_ = 0; }
    MouseAction Update(std::uint64_t now, bool foreground, bool gameplay, bool pistol) {
        if (!foreground) { Reset(); return MouseAction::none; }
        if (!gameplay) held_ = false;
        if (!held_ || !gameplay || !pistol) {
            if (!active_) return MouseAction::none;
            active_ = false;
            // Restore an ordinary held click when switching to a primary weapon.
            bool restore = held_ && gameplay;
            if (down_ == restore) return MouseAction::none;
            down_ = restore;
            return restore ? MouseAction::down : MouseAction::up;
        }
        if (!active_) { active_ = down_ = true; next_ = now + 35; return MouseAction::none; }
        if (now < next_) return MouseAction::none;
        next_ = now + 35;
        down_ = !down_;
        return down_ ? MouseAction::down : MouseAction::up;
    }
};
}
