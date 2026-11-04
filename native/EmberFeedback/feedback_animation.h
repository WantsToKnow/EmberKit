// Copyright 2026 EmberKit contributors. SPDX-License-Identifier: Apache-2.0
#pragma once
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include "feedback_logic.h"

namespace ember {
inline float Unit(float value) { return std::clamp(value, 0.0f, 1.0f); }
inline float Smooth(float value) { value = Unit(value); return value * value * (3 - 2 * value); }
struct SkullPose {
    float x{}, y{}, scale = 1, alpha{}, ringX{}, ringSize{}, ringAlpha{};
    bool headshot{};
};
struct SkullFrame { std::array<SkullPose, 6> skulls{}; int count{}; };

// Display coordinates are 1080p pixels relative to the HUD center. Keep each
// icon's current position across insertions, including kills in the same tick.
class SkullAnimation {
    struct State {
        std::uint64_t serial{}, born{}, exitAt{};
        float x{}, impactX{};
        bool headshot{};
    };
    std::array<State, 6> states_{};
    std::uint64_t previous_{};
public:
    void Reset() { states_ = {}; previous_ = 0; }
    SkullFrame Update(const Feedback& feedback, std::uint64_t latestSerial, std::uint64_t now) {
        SkullFrame frame;
        if (!feedback.visible) { Reset(); return frame; }
        // A new chain can begin between paint callbacks, without an empty frame.
        if (feedback.visible == 1 && states_[0].serial != latestSerial) Reset();
        float dt = previous_ && now >= previous_ ? std::min((now - previous_) / 1000.0f, 0.1f) : 0;
        previous_ = now;
        float follow = 1 - std::exp(-dt / 0.06f);
        const int count = std::min(feedback.visible, 3);
        std::array<State, 6> next{};
        for (int rank = 0; rank < count; ++rank) {
            auto serial = latestSerial - rank;
            const auto& kill = feedback.kills[feedback.kills.size() - 1 - rank];
            float target = (rank - (count - 1) / 2.0f) * 36;
            float age = now >= kill.time ? (now - kill.time) / 1000.0f : 0;
            auto found = std::find_if(states_.begin(), states_.end(), [serial](const State& state) { return state.serial == serial; });
            State state = found != states_.end() ? *found : State{ serial, kill.time, 0, target + 12, target + 12, kill.headshot };
            state.exitAt = 0;
            if (rank == 0 && age < 0.26f) state.x = state.impactX;
            else if (found == states_.end()) state.x = target + 12 * std::exp(-std::max(0.0f, age - 0.26f) / 0.06f);
            else state.x += (target - state.x) * follow;
            next[rank] = state;
        }
        int total = count;
        for (auto old : states_) {
            if (!old.serial || total == static_cast<int>(next.size())) continue;
            bool active = false;
            for (int rank = 0; rank < count; ++rank) active |= next[rank].serial == old.serial;
            if (active) continue;
            if (!old.exitAt) old.exitAt = now;
            if (now - old.exitAt >= 180) continue;
            old.x += ((count + 1) * 18.0f - old.x) * follow;
            next[total++] = old;
        }
        states_ = next;
        frame.count = total;
        for (int i = 0; i < total; ++i) {
            const auto& state = states_[i];
            auto& pose = frame.skulls[i];
            float age = now >= state.born ? (now - state.born) / 1000.0f : 0;
            pose.x = state.x; pose.ringX = state.impactX; pose.headshot = state.headshot;
            pose.alpha = Smooth(age / 0.04f);
            if (state.exitAt) pose.alpha *= 1 - Smooth((now - state.exitAt) / 180.0f);
            if (age < 0.18f) {
                float drop = Unit(age / 0.18f);
                pose.y = -32 * (1 - drop * drop);
                pose.scale = 2.15f - 1.07f * Smooth(drop);
            }
            else {
                float landed = age - 0.18f;
                pose.scale = 1 + 0.08f * std::exp(-landed * 18) * std::cos(landed * 35);
                if (landed < 0.16f) pose.y = -3 * std::sin(landed / 0.16f * 3.14159265f) * std::exp(-landed * 4);
                if (landed < 0.3f) {
                    float ring = Unit(landed / 0.3f);
                    float remaining = 1 - ring;
                    pose.ringSize = 24 + 52 * (1 - remaining * remaining * remaining);
                    pose.ringAlpha = 0.8f * (1 - ring) * (1 - ring) * pose.alpha;
                }
            }
        }
        return frame;
    }
};
}
