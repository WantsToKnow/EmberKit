// Copyright 2026 EmberKit contributors. SPDX-License-Identifier: Apache-2.0
#pragma once
#include <array>
#include <cstdint>
#include <cstring>

namespace ember {
enum class Kind { none, common, smoker, boomer, hunter, spitter, jockey, charger, tank, witch };
inline bool IsPlayerInfected(Kind kind) { return kind >= Kind::smoker && kind <= Kind::tank; }

inline Kind ZombieClass(int value) {
    switch (value) {
    case 1: return Kind::smoker; case 2: return Kind::boomer; case 3: return Kind::hunter;
    case 4: return Kind::spitter; case 5: return Kind::jockey; case 6: return Kind::charger;
    case 8: return Kind::tank; default: return Kind::none;
    }
}
inline Kind VictimName(const char* name) {
    if (!name) return Kind::none;
    char lower[32]{};
    for (size_t i = 0; i < sizeof(lower) - 1 && name[i]; ++i)
        lower[i] = name[i] >= 'A' && name[i] <= 'Z' ? static_cast<char>(name[i] + 32) : name[i];
    constexpr const char* names[] = { "", "infected", "smoker", "boomer", "hunter", "spitter", "jockey", "charger", "tank", "witch" };
    for (int i = 1; i < 10; ++i) if (std::strcmp(lower, names[i]) == 0) return static_cast<Kind>(i);
    return Kind::none;
}
inline Kind PlayerVictim(const char* networkClass, int team, int zombieClass) {
    if (!networkClass || team != 3) return Kind::none;
    if (std::strcmp(networkClass, "CTerrorPlayer") == 0) return ZombieClass(zombieClass);
    auto named = VictimName(networkClass);
    return IsPlayerInfected(named) ? named : Kind::none;
}
inline const wchar_t* Label(Kind kind) {
    constexpr const wchar_t* names[] = { L"", L"普通感染者", L"Smoker", L"Boomer", L"Hunter", L"Spitter", L"Jockey", L"Charger", L"Tank", L"Witch" };
    return names[static_cast<int>(kind)];
}

struct Kill { Kind kind{}; int identity{}; bool headshot{}; std::uint64_t time{}; };
struct Input { int attacker{}, local{}, localTeam{}, identity{}; Kind kind{}; bool headshot{}; };
enum class Decision {
    accepted, aborted, missingAttacker, noLocalPlayer, unresolvedAttacker, notLocalAttacker,
    notSurvivor, unknownVictim, noncanonicalVictim, scopeFiltered, invalidVictim, duplicate, count
};
inline const char* DecisionName(Decision decision) {
    constexpr const char* names[] = { "accepted", "aborted", "missing_attacker", "no_local_player",
        "unresolved_attacker", "attacker_not_local", "local_not_survivor", "unknown_victim",
        "noncanonical_victim", "scope_filtered", "invalid_victim", "duplicate" };
    return names[static_cast<size_t>(decision)];
}
class Feedback {
    struct Seen { Kind kind{}; int identity{}; std::uint64_t time{}; };
    std::array<Seen, 128> seen_{};
    size_t next_{};
public:
    bool all{};
    std::array<Kill, 6> kills{};
    int visible{}, chain{};
    std::uint64_t last{};

    void Reset() { seen_ = {}; next_ = 0; kills = {}; visible = chain = 0; last = 0; }
    bool Accept(const Input& input, std::uint64_t now, Decision* decision = nullptr) {
        auto reject = [decision](Decision value) { if (decision) *decision = value; return false; };
        if (input.local <= 0) return reject(Decision::noLocalPlayer);
        if (input.attacker <= 0) return reject(Decision::unresolvedAttacker);
        if (input.attacker != input.local) return reject(Decision::notLocalAttacker);
        if (input.localTeam != 2) return reject(Decision::notSurvivor);
        if (input.kind == Kind::none) return reject(Decision::unknownVictim);
        if (!all && input.kind == Kind::common) return reject(Decision::scopeFiltered);
        // The captured client infected_death events identify the attacker but
        // have infected_id=0. Each canonical common-death event can still
        // qualify; an unavailable victim ID is not a shared deduplication key.
        const bool identified = input.identity > 0;
        if (!identified && !(input.kind == Kind::common && input.identity == 0)) return reject(Decision::invalidVictim);
        if (now < last || now - last > 3000) Reset();
        if (identified) {
            for (const auto& seen : seen_)
                if (seen.identity == input.identity && seen.kind == input.kind && now >= seen.time && now - seen.time < 200) return reject(Decision::duplicate);
            seen_[next_++ % seen_.size()] = { input.kind, input.identity, now };
        }
        for (size_t i = 1; i < kills.size(); ++i) kills[i - 1] = kills[i];
        kills.back() = { input.kind, input.identity, input.headshot, now };
        if (visible < static_cast<int>(kills.size())) ++visible;
        if (chain < 9999) ++chain;
        last = now;
        if (decision) *decision = Decision::accepted;
        return true;
    }
};
}
