// Copyright 2026 EmberKit contributors. SPDX-License-Identifier: Apache-2.0
#include "feedback.cpp" // Test our decoders and export without loading any game module.
#define EMBER_LOADER_TESTS
#include "loader.cpp"
#include <cassert>
#include <iostream>
#include "feedback_logic.h"
#include "source_abi.h"

struct TestSurface {
    void** table;
    int texture{}, width{}, height{};
    const unsigned char* rgba{};
    int creations{}, deletions{};
    bool gameTextureDeleted{};
    std::array<bool, 2> live{};
};
int __fastcall CreateTexture(void* self, void*, bool procedural) {
    auto surface = static_cast<TestSurface*>(self);
    assert(procedural && surface->creations < 2);
    int index = surface->creations++;
    surface->live[index] = true;
    return 200 + index;
}
bool MockTextureValid(void* self, int texture) {
    auto surface = static_cast<TestSurface*>(self);
    if (texture == 1) return !surface->gameTextureDeleted;
    return texture >= 200 && texture < 202 && surface->live[texture - 200];
}
bool __fastcall DeleteTexture(void* self, void*, int texture) {
    auto surface = static_cast<TestSurface*>(self);
    if (texture == 1) { surface->gameTextureDeleted = true; return false; }
    if (!MockTextureValid(self, texture)) return false;
    surface->live[texture - 200] = false; ++surface->deletions;
    return false; // Match the pinned Surface031 wrapper's unreliable return.
}
void __fastcall Upload(void* self, void*, int texture, const unsigned char* rgba, int width, int height) {
    auto surface = static_cast<TestSurface*>(self);
    assert(MockTextureValid(self, texture));
    surface->texture = texture; surface->rgba = rgba; surface->width = width; surface->height = height;
}

struct TestClient { void** table; source::ClientClass* classes; int calls{}; };
source::ClientClass* __fastcall GetClasses(void* self, void*) {
    auto client = static_cast<TestClient*>(self);
    ++client->calls;
    return client->classes;
}
source::ClientClass* __fastcall WrongClientSlot(void*, void*) {
    assert(false && "VClient016 slot 6/8 is not the pinned game's class-list getter");
    return reinterpret_cast<source::ClientClass*>(1);
}
source::ClientClass* __fastcall WrongNetworkSlot(void*, void*) {
    assert(false && "Pinned IClientNetworkable slot 2 does not return a ClientClass");
    return nullptr;
}

int main(int argc, char** argv) {
    using namespace ember;
    Feedback feedback;
    Input common{ 1, 1, 2, 100, Kind::common, false };
    Input hunter{ 1, 1, 2, 5, Kind::hunter, true };
    Decision decision{};
    assert(!feedback.Accept(common, 1000, &decision) && decision == Decision::scopeFiltered);
    assert(feedback.Accept(hunter, 1000, &decision) && decision == Decision::accepted && feedback.chain == 1);
    assert(!feedback.Accept(hunter, 1010, &decision) && decision == Decision::duplicate);
    auto other = hunter; other.attacker = 2;
    assert(!feedback.Accept(other, 1500, &decision) && decision == Decision::notLocalAttacker);
    auto spectator = hunter; spectator.localTeam = 1;
    assert(!feedback.Accept(spectator, 1500, &decision) && decision == Decision::notSurvivor);
    auto unknown = hunter; unknown.kind = Kind::none;
    assert(!feedback.Accept(unknown, 1500, &decision) && decision == Decision::unknownVictim);
    auto invalid = hunter; invalid.identity = 0;
    assert(!feedback.Accept(invalid, 1500, &decision) && decision == Decision::invalidVictim);
    auto missing = hunter; missing.local = 0;
    assert(!feedback.Accept(missing, 1500, &decision) && decision == Decision::noLocalPlayer);
    missing = hunter; missing.attacker = 0;
    assert(!feedback.Accept(missing, 1500, &decision) && decision == Decision::unresolvedAttacker);
    feedback.all = true;
    assert(feedback.Accept(common, 1500) && feedback.chain == 2);
    Input witch{ 1, 1, 2, 400, Kind::witch, false };
    Input tank{ 1, 1, 2, 6, Kind::tank, false };
    feedback.all = false;
    assert(feedback.Accept(witch, 1600) && feedback.Accept(tank, 1700));
    assert(feedback.chain == 4 && feedback.kills.back().kind == Kind::tank);
    assert(feedback.Accept(hunter, 5000) && feedback.chain == 1);
    assert(feedback.Accept(common, 5100) == false);
    assert(feedback.Accept(hunter, 10) && feedback.chain == 1);
    feedback.Reset(); feedback.all = true;
    for (int i = 1; i <= 100; ++i) { common.identity = 1000 + i; assert(feedback.Accept(common, 1000 + i)); }
    assert(feedback.chain == 100 && feedback.visible == 6 && feedback.kills.front().identity == 1095 && feedback.kills.back().identity == 1100);
    feedback.Reset();
    Input unidentified{ 1, 1, 2, 0, Kind::common, false };
    for (int i = 0; i < 24; ++i) assert(feedback.Accept(unidentified, 1000 + i, &decision) && decision == Decision::accepted);
    assert(feedback.chain == 24 && feedback.visible == 6);
    unidentified.attacker = 2;
    assert(!feedback.Accept(unidentified, 1100, &decision) && decision == Decision::notLocalAttacker);
    unidentified.attacker = 1; unidentified.localTeam = 1;
    assert(!feedback.Accept(unidentified, 1100, &decision) && decision == Decision::notSurvivor);
    unidentified.localTeam = 2; feedback.all = false;
    assert(!feedback.Accept(unidentified, 1100, &decision) && decision == Decision::scopeFiltered);
    unidentified.kind = Kind::hunter;
    assert(!feedback.Accept(unidentified, 1100, &decision) && decision == Decision::invalidVictim);
    assert(VictimName("Hunter") == Kind::hunter && VictimName("infected") == Kind::common);
    assert(VictimName("Coach") == Kind::none && ZombieClass(7) == Kind::none && ZombieClass(8) == Kind::tank);
    assert(!IsPlayerInfected(Kind::common) && !IsPlayerInfected(Kind::witch) && IsPlayerInfected(Kind::hunter) && IsPlayerInfected(Kind::tank));
    for (const char* name : { "Smoker", "Boomer", "Hunter", "Spitter", "Jockey", "Charger", "Tank" }) {
        assert(PlayerVictim(name, 3, -1) == VictimName(name));
        assert(PlayerVictim(name, 2, -1) == Kind::none);
    }
    assert(PlayerVictim("CTerrorPlayer", 3, 8) == Kind::tank && PlayerVictim("CTerrorPlayer", 2, 8) == Kind::none);
    assert(PlayerVictim("Infected", 3, 3) == Kind::none && PlayerVictim("Witch", 3, 3) == Kind::none);
    source::RecvProp properties[2]{};
    properties[0].name = "m_iTeamNum"; properties[0].offset = 240;
    properties[1].name = "m_zombieClass"; properties[1].offset = 100;
    source::RecvTable table{ properties, 2 };
    source::ClientClass terror{ nullptr, nullptr, "CTerrorPlayer", &table };
    source::ClientClass jockey{ nullptr, nullptr, "Jockey", nullptr, &terror };
    void* clientMethods[9]{};
    clientMethods[6] = clientMethods[8] = reinterpret_cast<void*>(&WrongClientSlot);
    clientMethods[7] = reinterpret_cast<void*>(&GetClasses);
    TestClient client{ clientMethods, &jockey };
    auto classes = source::ClientClasses(&client);
    assert(client.calls == 1 && classes == &jockey && classes->next == &terror);
    assert(Property(classes->next->table, "m_iTeamNum") == 240 && Property(classes->next->table, "m_zombieClass") == 100);
    source::ClientClass smoker{ nullptr, nullptr, "Smoker" };
    void* networkMethods[3]{};
    networkMethods[1] = reinterpret_cast<void*>(&GetClasses);
    networkMethods[2] = reinterpret_cast<void*>(&WrongNetworkSlot);
    TestClient networkable{ networkMethods, &smoker };
    auto victimClass = source::NetworkClass(&networkable);
    assert(networkable.calls == 1 && victimClass == &smoker && PlayerVictim(victimClass->name, 3, -1) == Kind::smoker);
    assert(argc == 3);
    std::filesystem::path assets(argv[1]);
    std::ifstream replay(assets.parent_path() / L"tests/death-events-20261104.txt");
    assert(replay);
    std::string header; std::getline(replay, header);
    Feedback replayFeedback; replayFeedback.all = true;
    std::uint64_t at{}; Input recorded{}; int kind{}, replayHeadshot{}, expected{}, replayed{}, replayAccepted{};
    while (replay >> at >> recorded.attacker >> recorded.local >> recorded.localTeam >> recorded.identity >> kind >> replayHeadshot >> expected) {
        recorded.kind = static_cast<Kind>(kind); recorded.headshot = replayHeadshot != 0;
        bool accepted = replayFeedback.Accept(recorded, at);
        assert(accepted == (expected != 0)); ++replayed; replayAccepted += accepted;
    }
    assert(replay.eof() && replayed == 40 && replayAccepted == 24);
    Image normal, headshot;
    assert(ReadImage(assets / L"skull.png", normal) && ReadImage(assets / L"headshot.png", headshot));
    assert(normal.width > 0 && normal.width < 1024 && normal.height < 1024 && normal.pixels.size() == static_cast<size_t>(normal.width) * normal.height * 4);
    void* surfaceMethods[73]{}; surfaceMethods[28] = reinterpret_cast<void*>(&Upload);
    surfaceMethods[33] = reinterpret_cast<void*>(&DeleteTexture);
    surfaceMethods[34] = reinterpret_cast<void*>(&CreateTexture);
    TestSurface surface{ surfaceMethods };
    // Surface031 slot 33 must never be called to create a texture. In the
    // reported crash that call deleted the game's existing texture ID 1.
    int normalTexture = source::CreateTexture(&surface);
    int headTexture = source::CreateTexture(&surface);
    assert(normalTexture == 200 && headTexture == 201 && surface.creations == 2 && surface.deletions == 0 && !surface.gameTextureDeleted);
    source::UploadTexture(&surface, normalTexture, normal.pixels.data(), normal.width, normal.height);
    assert(surface.texture == normalTexture && surface.rgba == normal.pixels.data() && surface.width == normal.width && surface.height == normal.height);
    source::UploadTexture(&surface, headTexture, headshot.pixels.data(), headshot.width, headshot.height);
    assert(surface.texture == headTexture && surface.rgba == headshot.pixels.data());
    assert(MockTextureValid(&surface, normalTexture) && MockTextureValid(&surface, headTexture) && MockTextureValid(&surface, 1));
    source::DeleteTexture(&surface, normalTexture);
    source::DeleteTexture(&surface, headTexture);
    assert(surface.deletions == 2 && !MockTextureValid(&surface, normalTexture) && !MockTextureValid(&surface, headTexture)
        && MockTextureValid(&surface, 1) && !surface.gameTextureDeleted);
    Wave killSound, headSound;
    assert(ReadWave(assets / L"kill.wav", killSound) && ReadWave(assets / L"headshot.wav", headSound));
    assert(killSound.format.nSamplesPerSec == 48000 && killSound.format.nChannels == 2 && killSound.data.size() == 391680);
    assert(!ReadWave(assets / L"skull.png", killSound));
    assert(EmberFeedback_Start(nullptr) == 2 && EmberFeedback_Start(const_cast<wchar_t*>(L"bad")) == 2);
    std::filesystem::path fixtures = std::filesystem::absolute(argv[2]);
    // Exercise the actual diagnostic writers without loading Source or raising
    // a real exception. The callback filter must always continue exception search.
    stateDirectory = fixtures / L"diagnostic-check";
    std::filesystem::create_directories(stateDirectory);
    sessionId.assign(32, 'D'); phase = "listening";
    lastEvent = {}; lastEvent.name = "infected_death"; lastEvent.attackerRaw = -1;
    deathEvents = 2;
    TraceDeath(Decision::missingAttacker);
    lastEvent.attackerRaw = 77; lastEvent.input = common;
    lastEvent.input.local = 4; lastEvent.input.attacker = 5;
    TraceDeath(Decision::notLocalAttacker);
    // Detailed samples are limited; aggregate counters remain complete.
    traceSamples = 64; nextTrace = GetTickCount64() + 60000;
    for (int i = 0; i < 100; ++i) TraceDeath(Decision::notLocalAttacker);
    assert(traceSamples == 64 && decisions[static_cast<size_t>(Decision::notLocalAttacker)] == 101);
    deathEvents = 102; renderState = "no_accepted_kill";
    Report(true);
    auto statusPath = stateDirectory / ("session-" + sessionId + ".json");
    std::ifstream statusFile(statusPath);
    std::string status((std::istreambuf_iterator<char>(statusFile)), {});
    assert(status.find("\"attacker_not_local\":101") != std::string::npos && status.find("\"missing_attacker\":1") != std::string::npos);
    assert(status.find("\"local\":4,\"attacker\":5") != std::string::npos && status.find("\"renderState\":\"no_accepted_kill\"") != std::string::npos);
    auto exceptionPath = stateDirectory / L"exceptions.log";
    exceptionLog = CreateFileW(exceptionPath.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    assert(exceptionLog != INVALID_HANDLE_VALUE);
    EXCEPTION_RECORD record{}; record.ExceptionCode = EXCEPTION_ACCESS_VIOLATION;
    record.ExceptionAddress = reinterpret_cast<void*>(&RecordException); record.NumberParameters = 2; record.ExceptionInformation[1] = 9;
    EXCEPTION_POINTERS pointers{ &record, nullptr };
    {
        Operation context("check.event.player_for_attacker");
        assert(RecordException(&pointers) == EXCEPTION_CONTINUE_SEARCH);
    }
    assert(currentOperation == nullptr);
    CloseHandle(exceptionLog); exceptionLog = INVALID_HANDLE_VALUE;
    std::ifstream exceptionFile(exceptionPath);
    std::string exceptionText((std::istreambuf_iterator<char>(exceptionFile)), {});
    assert(exceptionText.find("code=0xC0000005") != std::string::npos && exceptionText.find("target=0x9") != std::string::npos
        && exceptionText.find("operation=check.event.player_for_attacker") != std::string::npos);
    std::ifstream traceFile(stateDirectory / L"EmberFeedback.log");
    std::string traceText((std::istreambuf_iterator<char>(traceFile)), {});
    assert(traceText.find("result=missing_attacker raw_attacker=-1") != std::string::npos && traceText.find("result=attacker_not_local") != std::string::npos);
    sessionId.clear(); stateDirectory.clear();
    auto hostPath = fixtures / L"EmberFeedback.FixtureHost.exe";
    auto dllPath = fixtures / L"EmberFeedback.dll";
    auto command = L"\"" + hostPath.wstring() + L"\"";
    STARTUPINFOW startup{}; startup.cb = sizeof(startup);
    PROCESS_INFORMATION child{};
    assert(CreateProcessW(hostPath.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, fixtures.c_str(), &startup, &child));
    ember_loader::Handle host(child.hProcess), initialThread(child.hThread);
    // A console fixture has no GUI input-idle signal. Its named event proves
    // that the CRT/loader and main entry point have finished startup.
    auto readyName = L"Local\\EmberFeedbackFixture.Exit." + std::to_wstring(child.dwProcessId);
    HANDLE ready{};
    for (int attempt = 0; attempt < 100 && !ready; ++attempt) { ready = OpenEventW(EVENT_MODIFY_STATE, FALSE, readyName.c_str()); if (!ready) Sleep(25); }
    assert(ready); CloseHandle(ready);
    std::wstring testSession(32, L'A');
    try { ember_loader::Attach(child.dwProcessId, hostPath, dllPath, testSession); }
    catch (const std::exception& error) {
        std::cerr << "Fixture loader failure: " << error.what() << std::endl;
        auto stopName = L"Local\\EmberFeedbackFixture.Exit." + std::to_wstring(child.dwProcessId);
        if (auto stop = OpenEventW(EVENT_MODIFY_STATE, FALSE, stopName.c_str())) { SetEvent(stop); CloseHandle(stop); }
        WaitForSingleObject(host, 5000);
        return 1;
    }
    std::ifstream receipt(fixtures / L"loader-receipt.txt");
    std::string receiptSession; DWORD receiptPid{}; receipt >> receiptSession >> receiptPid;
    assert(receiptSession == std::string(32, 'A') && receiptPid == child.dwProcessId);
    bool duplicateRejected = false;
    try { ember_loader::Attach(child.dwProcessId, hostPath, dllPath, testSession); }
    catch (const std::runtime_error&) { duplicateRejected = true; }
    assert(duplicateRejected);
    std::wstring stopName = L"Local\\EmberFeedbackFixture.Exit." + std::to_wstring(child.dwProcessId);
    HANDLE stop{};
    for (int attempt = 0; attempt < 20 && !stop; ++attempt) { stop = OpenEventW(EVENT_MODIFY_STATE, FALSE, stopName.c_str()); if (!stop) Sleep(25); }
    assert(stop); SetEvent(stop); CloseHandle(stop);
    assert(WaitForSingleObject(host, 5000) == WAIT_OBJECT_0);
    std::cout << "PASS: Surface031 create/upload/delete lifecycle preserves the game's texture 1; native ownership/scopes/combo and 40-event replay (24 accepted); named infected and NetworkClass slot 1; diagnostic counters/exception context; VClient016 ABI; PNG/WAV; isolated DLL load/start and duplicate rejection. No game or Steam loaded.\n";
}
