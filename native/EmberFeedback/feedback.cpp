// Copyright 2026 EmberKit contributors. SPDX-License-Identifier: Apache-2.0
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <bcrypt.h>
#include <wincodec.h>
#include <xaudio2.h>
#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <string>
#include <vector>
#include "source_abi.h"
#include "feedback_logic.h"
#include "feedback_animation.h"
#include "pistol_input.h"

namespace {
HMODULE ownModule;
std::filesystem::path moduleDirectory, stateDirectory;
std::string sessionId;
std::string phase = "starting", failure;
unsigned long long paintFrames{}, deathEvents{}, acceptedKills{}, hudDraws{};
int registeredEvents{}, scope = 1;
bool audioReady{};
bool feedbackEnabled = true, autoPistolEnabled{};
const char* pistolState = "disabled";
ember::PistolClicker pistolInput;
ULONGLONG nextReport{};
constexpr const char* debugVersion = "autopistol-20261104-1";
// Keep checkpoints observable to the hardware-exception filter in /O2 builds.
thread_local const char* volatile currentOperation{};
struct Operation {
    const char* previous = currentOperation;
    explicit Operation(const char* name) { currentOperation = name; }
    ~Operation() { currentOperation = previous; }
};
HANDLE exceptionLog = INVALID_HANDLE_VALUE;
DWORD ownImageSize{};
struct EventDebug {
    const char* name = "none";
    int attackerRaw = -1, userId = -1, infectedId = -1, witchId = -1, entityId = -1;
    int victimIndex{}, victimTeam = -1, zombieClass = -1;
    bool victimEntity{}, localEntity{};
    int victimIsBot = -1;
    char networkClass[48]{};
    ember::Input input{};
};
EventDebug lastEvent;
std::array<unsigned long long, static_cast<size_t>(ember::Decision::count)> decisions{};
const char* lastDecision = "none";
const char* renderState = "not_initialized";
int screenWidth{}, screenHeight{}, debugTeamOffset = -1, debugZombieOffset = -1;
unsigned long long audioRequests{}, audioStarts{};
unsigned traceSamples{};
ULONGLONG nextTrace{}, nextHeartbeat{};

// Called only by our callback SEH filters. Never suppresses the exception or
// replaces the game's crash handler. The file is opened before callbacks start;
// this path uses a fixed buffer and WriteFile, not C++ streams or Source calls.
LONG RecordException(EXCEPTION_POINTERS* exception) {
    if (!exception || !exception->ExceptionRecord || exceptionLog == INVALID_HANDLE_VALUE) return EXCEPTION_CONTINUE_SEARCH;
    const auto& record = *exception->ExceptionRecord;
    if (record.ExceptionCode != EXCEPTION_ACCESS_VIOLATION && record.ExceptionCode != EXCEPTION_ILLEGAL_INSTRUCTION
        && record.ExceptionCode != EXCEPTION_INT_DIVIDE_BY_ZERO) return EXCEPTION_CONTINUE_SEARCH;
    const auto address = reinterpret_cast<ULONG_PTR>(record.ExceptionAddress);
    const auto base = reinterpret_cast<ULONG_PTR>(ownModule);
    const bool ours = base && address >= base && address - base < ownImageSize;
    char line[1024]{};
    int size = std::snprintf(line, sizeof(line),
        "tick=%llu pid=%lu tid=%lu code=0x%08lX ip=%p dll_base=%p own_ip=%d own_rva=0x%lX access=%llu target=0x%llX operation=%s event=%s attacker_raw=%d local=%d attacker=%d team=%d victim=%d decision=%s\r\n",
        GetTickCount64(), GetCurrentProcessId(), GetCurrentThreadId(), record.ExceptionCode,
        record.ExceptionAddress, static_cast<void*>(ownModule), ours, ours ? static_cast<DWORD>(address - base) : 0,
        record.NumberParameters > 0 ? static_cast<unsigned long long>(record.ExceptionInformation[0]) : 0,
        record.NumberParameters > 1 ? static_cast<unsigned long long>(record.ExceptionInformation[1]) : 0,
        currentOperation ? currentOperation : "none", lastEvent.name, lastEvent.attackerRaw, lastEvent.input.local,
        lastEvent.input.attacker, lastEvent.input.localTeam, lastEvent.input.identity, lastDecision);
    if (size > 0) { DWORD written{}; WriteFile(exceptionLog, line, static_cast<DWORD>(std::min(size, static_cast<int>(sizeof(line) - 1))), &written, nullptr); }
    return EXCEPTION_CONTINUE_SEARCH;
}
void Report(bool force = false) {
    Operation context("debug.status_write");
    auto now = GetTickCount64();
    if (sessionId.empty() || (!force && now < nextReport)) return;
    nextReport = now + 1000;
    auto target = stateDirectory / ("session-" + sessionId + ".json");
    auto temporary = target; temporary += L".tmp";
    std::ofstream file(temporary, std::ios::trunc);
    file << "{\"session\":\"" << sessionId << "\",\"pid\":" << GetCurrentProcessId()
        << ",\"phase\":\"" << phase << "\",\"error\":\"" << failure << "\",\"scope\":" << scope
        << ",\"paintFrames\":" << paintFrames << ",\"deathEvents\":" << deathEvents
        << ",\"acceptedKills\":" << acceptedKills << ",\"hudDraws\":" << hudDraws
        << ",\"registeredEvents\":" << registeredEvents << ",\"audio\":" << (audioReady ? "true" : "false")
        << ",\"killFeedbackEnabled\":" << (feedbackEnabled ? "true" : "false")
        << ",\"autoPistolEnabled\":" << (autoPistolEnabled ? "true" : "false")
        << ",\"autoPistolReady\":" << (pistolInput.Ready() ? "true" : "false")
        << ",\"autoPistolActive\":" << (pistolInput.Active() ? "true" : "false")
        << ",\"autoPistolClicks\":" << pistolInput.Clicks() << ",\"autoPistolError\":" << pistolInput.Error()
        << ",\"autoPistolState\":\"" << pistolState << "\""
        << ",\"debugVersion\":\"" << debugVersion << "\",\"lastDecision\":\"" << lastDecision
        << "\",\"renderState\":\"" << renderState << "\",\"screenWidth\":" << screenWidth << ",\"screenHeight\":" << screenHeight
        << ",\"teamOffset\":" << debugTeamOffset << ",\"zombieOffset\":" << debugZombieOffset
        << ",\"audioRequests\":" << audioRequests << ",\"audioStarts\":" << audioStarts << ",\"decisionCounts\":{";
    for (size_t i = 0; i < decisions.size(); ++i) {
        if (i) file << ',';
        file << '"' << ember::DecisionName(static_cast<ember::Decision>(i)) << "\":" << decisions[i];
    }
    const auto& e = lastEvent;
    file << "},\"lastEvent\":{\"name\":\"" << e.name << "\",\"attackerRaw\":" << e.attackerRaw << ",\"userId\":" << e.userId
        << ",\"infectedId\":" << e.infectedId << ",\"witchId\":" << e.witchId << ",\"entityId\":" << e.entityId
        << ",\"local\":" << e.input.local << ",\"attacker\":" << e.input.attacker << ",\"localTeam\":" << e.input.localTeam
        << ",\"victim\":" << e.input.identity << ",\"kind\":" << static_cast<int>(e.input.kind)
        << ",\"victimIndex\":" << e.victimIndex << ",\"victimTeam\":" << e.victimTeam << ",\"zombieClass\":" << e.zombieClass
        << ",\"networkClass\":\"" << e.networkClass << "\",\"localEntity\":" << (e.localEntity ? "true" : "false")
        << ",\"victimEntity\":" << (e.victimEntity ? "true" : "false") << ",\"headshot\":" << (e.input.headshot ? "true" : "false")
        << ",\"victimIsBot\":" << e.victimIsBot << "}}\n";
    file.flush(); bool ok = file.good(); file.close();
    if (ok) MoveFileExW(temporary.c_str(), target.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH);
}
void Log(const char* text, bool echo = true) {
    using Msg = void(__cdecl*)(const char*, ...);
    static auto msg = reinterpret_cast<Msg>(GetProcAddress(GetModuleHandleW(L"tier0.dll"), "Msg"));
    if (msg && echo) msg("[EmberFeedback] %s\n", text);
    if (!stateDirectory.empty()) {
        std::ofstream file(stateDirectory / L"EmberFeedback.log", std::ios::app);
        SYSTEMTIME time{}; GetLocalTime(&time);
        char prefix[160]{};
        std::snprintf(prefix, sizeof(prefix), "%04u-%02u-%02u %02u:%02u:%02u.%03u tick=%llu pid=%lu tid=%lu session=%s ",
            time.wYear, time.wMonth, time.wDay, time.wHour, time.wMinute, time.wSecond, time.wMilliseconds,
            GetTickCount64(), GetCurrentProcessId(), GetCurrentThreadId(), sessionId.c_str());
        file << prefix << text << "\n";
    }
}
void TraceDeath(ember::Decision decision) {
    Operation context("debug.death_trace");
    ++decisions[static_cast<size_t>(decision)];
    lastDecision = ember::DecisionName(decision);
    const auto now = GetTickCount64();
    // Keep the first 64 decisions, then at most one detailed sample per 2s.
    // Every decision still contributes to the JSON counters, including swarms.
    if (traceSamples < 64 || now >= nextTrace) {
        ++traceSamples; nextTrace = now + 2000;
        const auto& e = lastEvent;
        char line[768]{};
        std::snprintf(line, sizeof(line),
            "DEATH name=%s result=%s raw_attacker=%d raw_userid=%d raw_infected_id=%d raw_witchid=%d raw_entityid=%d local=%d attacker=%d local_team=%d local_entity=%d victim=%d kind=%d victim_index=%d victim_entity=%d class=%s victim_team=%d zombie_class=%d headshot=%d victim_is_bot=%d",
            e.name, lastDecision, e.attackerRaw, e.userId, e.infectedId, e.witchId, e.entityId, e.input.local,
            e.input.attacker, e.input.localTeam, e.localEntity, e.input.identity, static_cast<int>(e.input.kind),
            e.victimIndex, e.victimEntity, e.networkClass, e.victimTeam, e.zombieClass, e.input.headshot, e.victimIsBot);
        Log(line, false);
    }
    Report();
}
void Heartbeat() {
    auto now = GetTickCount64();
    if (now < nextHeartbeat) return;
    nextHeartbeat = now + 10000;
    char line[384]{};
    std::snprintf(line, sizeof(line), "HEARTBEAT phase=%s paint=%llu deaths=%llu accepted=%llu hud=%llu events=%d last_decision=%s render=%s audio_requests=%llu audio_starts=%llu",
        phase.c_str(), paintFrames, deathEvents, acceptedKills, hudDraws, registeredEvents, lastDecision, renderState, audioRequests, audioStarts);
    Log(line, false);
}
void* Interface(const wchar_t* module, const char* name) {
    auto factory = reinterpret_cast<source::Factory>(GetProcAddress(GetModuleHandleW(module), "CreateInterface"));
    auto object = factory ? factory(name, nullptr) : nullptr;
    char line[256]{}; std::snprintf(line, sizeof(line), "INTERFACE name=%s object=%p vtable=%p", name, object, object ? *static_cast<void***>(object) : nullptr);
    Log(line, false);
    return object;
}
bool SupportedModules() {
    for (const auto& pin : source::pinned) {
        wchar_t path[32768]{};
        if (!GetModuleFileNameW(GetModuleHandleW(pin.name), path, static_cast<DWORD>(std::size(path)))) return false;
        std::ifstream file(path, std::ios::binary);
        BCRYPT_ALG_HANDLE algorithm{};
        BCRYPT_HASH_HANDLE hash{};
        if (!file || BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0) return false;
        bool ok = BCryptCreateHash(algorithm, &hash, nullptr, 0, nullptr, 0, 0) >= 0;
        std::array<unsigned char, 65536> buffer{};
        while (ok && file) {
            file.read(reinterpret_cast<char*>(buffer.data()), buffer.size());
            if (file.gcount()) ok = BCryptHashData(hash, buffer.data(), static_cast<ULONG>(file.gcount()), 0) >= 0;
        }
        unsigned char digest[32]{};
        ok = ok && file.eof() && BCryptFinishHash(hash, digest, sizeof(digest), 0) >= 0;
        if (hash) BCryptDestroyHash(hash);
        BCryptCloseAlgorithmProvider(algorithm, 0);
        char hex[65]{};
        for (int i = 0; i < 32; ++i) sprintf_s(hex + i * 2, 3, "%02x", digest[i]);
        if (!ok || std::strcmp(hex, pin.hash) != 0) return false;
    }
    return true;
}

struct Image { int width{}, height{}; std::vector<unsigned char> pixels; };
Image RingImage() {
    Image image{ 128, 128, std::vector<unsigned char>(128 * 128 * 4, 255) };
    for (int y = 0; y < image.height; ++y) for (int x = 0; x < image.width; ++x) {
        float dx = x - 63.5f, dy = y - 63.5f;
        float edge = std::abs(std::sqrt(dx * dx + dy * dy) - 56);
        image.pixels[(static_cast<size_t>(y) * image.width + x) * 4 + 3] = static_cast<unsigned char>(255 * ember::Unit(2 - edge));
    }
    return image;
}
bool ReadImage(const std::filesystem::path& path, Image& image) {
    IWICImagingFactory* factory{};
    IWICBitmapDecoder* decoder{};
    IWICBitmapFrameDecode* frame{};
    IWICFormatConverter* converter{};
    HRESULT initialized = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    HRESULT result = CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory));
    if (SUCCEEDED(result)) result = factory->CreateDecoderFromFilename(path.c_str(), nullptr, GENERIC_READ, WICDecodeMetadataCacheOnLoad, &decoder);
    if (SUCCEEDED(result)) result = decoder->GetFrame(0, &frame);
    UINT width{}, height{};
    if (SUCCEEDED(result)) result = frame->GetSize(&width, &height);
    if (!width || !height || width > 4096 || height > 4096) result = E_INVALIDARG;
    if (SUCCEEDED(result)) result = factory->CreateFormatConverter(&converter);
    if (SUCCEEDED(result)) result = converter->Initialize(frame, GUID_WICPixelFormat32bppRGBA, WICBitmapDitherTypeNone, nullptr, 0, WICBitmapPaletteTypeCustom);
    std::vector<unsigned char> raw;
    if (SUCCEEDED(result)) {
        raw.resize(static_cast<size_t>(width) * height * 4);
        result = converter->CopyPixels(nullptr, width * 4, static_cast<UINT>(raw.size()), raw.data());
    }
    if (converter) converter->Release();
    if (frame) frame->Release();
    if (decoder) decoder->Release();
    if (factory) factory->Release();
    if (SUCCEEDED(initialized)) CoUninitialize();
    if (FAILED(result)) return false;
    // The supplied icons have large transparent margins. Crop by alpha, preserving RGBA.
    int left = static_cast<int>(width), top = static_cast<int>(height), right = -1, bottom = -1;
    for (int y = 0; y < static_cast<int>(height); ++y) for (int x = 0; x < static_cast<int>(width); ++x)
        if (raw[(static_cast<size_t>(y) * width + x) * 4 + 3]) {
            left = std::min(left, x); top = std::min(top, y); right = std::max(right, x); bottom = std::max(bottom, y);
        }
    if (right < left) return false;
    image.width = right - left + 1; image.height = bottom - top + 1;
    image.pixels.resize(static_cast<size_t>(image.width) * image.height * 4);
    for (int y = 0; y < image.height; ++y)
        std::memcpy(image.pixels.data() + static_cast<size_t>(y) * image.width * 4,
            raw.data() + (static_cast<size_t>(y + top) * width + left) * 4, static_cast<size_t>(image.width) * 4);
    return true;
}
struct Wave { WAVEFORMATEX format{}; std::vector<unsigned char> data; };
bool ReadWave(const std::filesystem::path& path, Wave& wave) {
    std::ifstream file(path, std::ios::binary | std::ios::ate);
    if (!file || file.tellg() < 12 || file.tellg() > 8 * 1024 * 1024) return false;
    std::vector<unsigned char> bytes(static_cast<size_t>(file.tellg()));
    file.seekg(0); file.read(reinterpret_cast<char*>(bytes.data()), bytes.size());
    if (!file || std::memcmp(bytes.data(), "RIFF", 4) || std::memcmp(bytes.data() + 8, "WAVE", 4)) return false;
    bool formatFound = false;
    for (size_t pos = 12; pos + 8 <= bytes.size();) {
        unsigned long size{}; std::memcpy(&size, bytes.data() + pos + 4, 4);
        if (size > bytes.size() - pos - 8) return false;
        if (!std::memcmp(bytes.data() + pos, "fmt ", 4) && size >= 16) {
            std::memcpy(&wave.format, bytes.data() + pos + 8, 16); formatFound = true;
        } else if (!std::memcmp(bytes.data() + pos, "data", 4))
            wave.data.assign(bytes.begin() + pos + 8, bytes.begin() + pos + 8 + size);
        pos += 8 + static_cast<size_t>(size) + (size & 1);
    }
    const auto& f = wave.format;
    return formatFound && f.wFormatTag == WAVE_FORMAT_PCM && f.nChannels >= 1 && f.nChannels <= 2
        && f.wBitsPerSample == 16 && f.nSamplesPerSec >= 8000 && f.nSamplesPerSec <= 96000
        && f.nBlockAlign == f.nChannels * 2 && f.nAvgBytesPerSec == f.nBlockAlign * f.nSamplesPerSec
        && !wave.data.empty() && wave.data.size() % f.nBlockAlign == 0;
}
class Audio {
    IXAudio2* engine_{};
    IXAudio2MasteringVoice* master_{};
    std::array<IXAudio2SourceVoice*, 4> voices_{};
    std::array<Wave, 2> waves_;
    unsigned next_{};
public:
    bool Init() {
        if (!ReadWave(moduleDirectory / L"assets/kill.wav", waves_[0]) || !ReadWave(moduleDirectory / L"assets/headshot.wav", waves_[1])
            || std::memcmp(&waves_[0].format, &waves_[1].format, sizeof(WAVEFORMATEX))) return false;
        if (FAILED(XAudio2Create(&engine_)) || FAILED(engine_->CreateMasteringVoice(&master_))) { Stop(); return false; }
        for (auto& voice : voices_) {
            if (FAILED(engine_->CreateSourceVoice(&voice, &waves_[0].format))) { Stop(); return false; }
            voice->SetVolume(0.25f);
        }
        return true;
    }
    HRESULT Play(bool headshot) {
        Operation context("audio.play");
        if (!engine_ || !voices_[0]) return E_UNEXPECTED;
        auto voice = voices_[next_++ % voices_.size()];
        voice->Stop(); voice->FlushSourceBuffers();
        const auto& wave = waves_[headshot ? 1 : 0];
        XAUDIO2_BUFFER buffer{}; buffer.Flags = XAUDIO2_END_OF_STREAM;
        buffer.AudioBytes = static_cast<UINT32>(wave.data.size()); buffer.pAudioData = wave.data.data();
        auto result = voice->SubmitSourceBuffer(&buffer);
        return SUCCEEDED(result) ? voice->Start() : result;
    }
    void Stop() {
        for (auto& voice : voices_) if (voice) { voice->DestroyVoice(); voice = nullptr; }
        if (master_) { master_->DestroyVoice(); master_ = nullptr; }
        if (engine_) { engine_->Release(); engine_ = nullptr; }
    }
};

int Property(source::RecvTable* table, const char* name, int depth = 0) {
    if (!table || depth > 16 || table->count < 0 || table->count > 4096) return -1;
    for (int i = 0; i < table->count; ++i) {
        const auto& prop = table->props[i];
        if (prop.name && !std::strcmp(prop.name, name) && prop.type == 0 && prop.offset >= 0) return prop.offset;
        if (prop.type == 6 && prop.table) {
            int nested = Property(prop.table, name, depth + 1);
            if (nested >= 0) return prop.offset + nested;
        }
    }
    return -1;
}
using Paint = void(__thiscall*)(void*, unsigned int, bool, bool);
class Plugin;
void __fastcall PaintHook(void*, void*, unsigned int, bool, bool);

class Plugin final : public source::Listener {
    void* client_{};
    void* engine_{};
    void* entities_{};
    void* events_{};
    void* panel_{};
    void* surface_{};
    void* engineVgui_{};
    void** paintSlot_{};
    Paint originalPaint_{};
    int teamOffset_ = -1, zombieOffset_ = -1;
    int activeWeaponOffset_ = -1, lifeOffset_ = -1, clipOffset_ = -1;
    std::array<Image, 3> images_;
    std::array<int, 3> textures_{ -1, -1, -1 };
    unsigned long font_{};
    int fontHeight_{};
    bool active_{}, paintLogged_{}, subscriptionsLogged_{}, ownsCom_{};
    std::atomic<bool> pending_{ false };
    ULONGLONG nextSubscribe_{};
    Audio audio_;
    ember::Feedback feedback_;
    ember::SkullAnimation animation_;
    static constexpr const char* eventNames_[] = {
        "player_death", "infected_death", "witch_killed", "round_start", "round_end", "map_transition",
        "player_team", "player_bot_replace", "bot_player_replace", "server_spawn"
    };
    void* Entity(int index) {
        Operation context("entity.get");
        return index > 0 && index <= 4096 ? source::Call<void*>(entities_, 3, index) : nullptr;
    }
    void UpdatePistol() {
        if (!autoPistolEnabled) return;
        Operation context("pistol.context");
        bool playing = active_ && source::Call<bool>(engine_, 26) && source::Call<bool>(engine_, 27)
            && !source::Call<bool>(engine_, 11) && !source::Call<bool>(engineVgui_, 2);
        pistolState = "not_playing";
        bool pistol = false;
        if (playing) {
            int local = source::Call<int>(engine_, 12);
            auto player = static_cast<unsigned char*>(Entity(local));
            playing = player && Team(local) == 2 && player[lifeOffset_] == 0;
            pistolState = "not_living_survivor";
            if (playing) {
                currentOperation = "pistol.active_weapon";
                auto handle = *reinterpret_cast<unsigned int*>(player + activeWeaponOffset_);
                int index = static_cast<int>(handle & 0xfff);
                auto weapon = static_cast<unsigned char*>(Entity(index));
                auto networkable = weapon ? source::Call<void*>(entities_, 0, index) : nullptr;
                auto klass = networkable ? source::NetworkClass(networkable) : nullptr;
                bool isPistol = klass && klass->name && !std::strcmp(klass->name, "CPistol");
                pistolState = isPistol ? "empty" : "not_pistol";
                pistol = isPistol && *reinterpret_cast<int*>(weapon + clipOffset_) > 0;
                if (pistol) pistolState = "ready";
            }
        }
        pistolInput.SetContext(playing, pistol);
    }
    int Team(int index, bool* exists = nullptr) {
        Operation context("entity.team_property");
        auto entity = static_cast<unsigned char*>(Entity(index));
        if (exists) *exists = entity != nullptr;
        return entity && teamOffset_ >= 0 ? *reinterpret_cast<int*>(entity + teamOffset_) : 0;
    }
    ember::Kind Victim(source::GameEvent* event) {
        Operation context("victim.player_for_userid");
        int uid = lastEvent.userId;
        int index = uid > 0 ? source::Call<int>(engine_, 9, uid) : std::max(0, lastEvent.entityId);
        lastEvent.victimIndex = index;
        lastEvent.input.identity = index > 0 ? index : (uid > 0 && uid < 65536 ? uid + 0x10000 : 0);
        auto entity = static_cast<unsigned char*>(Entity(index));
        lastEvent.victimEntity = entity != nullptr;
        currentOperation = "victim.get_networkable";
        auto networkable = index > 0 ? source::Call<void*>(entities_, 0, index) : nullptr;
        currentOperation = "victim.get_client_class";
        auto klass = networkable ? source::NetworkClass(networkable) : nullptr;
        currentOperation = "victim.class_name";
        if (entity && klass && klass->name) {
            // Only engine class labels are recorded; no player names or chat.
            for (size_t i = 0; i + 1 < std::size(lastEvent.networkClass) && klass->name[i]; ++i) {
                char c = klass->name[i];
                lastEvent.networkClass[i] = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' ? c : '?';
            }
            const bool genericPlayer = !std::strcmp(klass->name, "CTerrorPlayer");
            if (genericPlayer || ember::IsPlayerInfected(ember::VictimName(klass->name))) {
                lastEvent.victimTeam = Team(index);
                if (lastEvent.victimTeam != 3) return ember::Kind::none;
                if (genericPlayer && zombieOffset_ >= 0) {
                    currentOperation = "victim.zombie_class_property";
                    lastEvent.zombieClass = *reinterpret_cast<int*>(entity + zombieOffset_);
                }
                return ember::PlayerVictim(klass->name, lastEvent.victimTeam, lastEvent.zombieClass);
            }
            if (!std::strcmp(klass->name, "Infected")) return ember::Kind::common;
            if (!std::strcmp(klass->name, "Witch")) return ember::Kind::witch;
            return ember::Kind::none;
        }
        // The entity may already be gone. Only explicit AI victim labels qualify for this fallback.
        currentOperation = "victim.fallback_fields";
        lastEvent.victimIsBot = event->GetBool("victimisbot");
        return lastEvent.victimIsBot > 0 ? ember::VictimName(event->GetString("victimname")) : ember::Kind::none;
    }
    void Subscribe() {
        Operation context("events.subscribe");
        auto now = GetTickCount64();
        if (now < nextSubscribe_) return;
        nextSubscribe_ = now + 1000;
        int ready = 0;
        registeredEvents = 0;
        for (size_t i = 0; i < std::size(eventNames_); ++i) {
            auto name = eventNames_[i];
            bool found = source::Call<bool>(events_, 4, static_cast<source::Listener*>(this), name);
            bool added = !found && source::Call<bool>(events_, 3, static_cast<source::Listener*>(this), name, false);
            if (!subscriptionsLogged_ || added || !found) {
                char line[160]{}; std::snprintf(line, sizeof(line), "SUBSCRIBE name=%s found=%d added=%d", name, found, added);
                Log(line, false);
            }
            if (found || added) {
                ++ready;
                if (i < 3) ++registeredEvents;
            }
        }
        if (active_) phase = registeredEvents ? "listening" : "initialized";
        if (ready == std::size(eventNames_) && !subscriptionsLogged_) { Log("Client event listeners ready."); subscriptionsLogged_ = true; }
    }
    void DrawText(const wchar_t* text, int x, int y, int alpha) {
        Operation context("render.text_size");
        int width{}, height{}; source::TextSize(surface_, font_, text, width, height);
        currentOperation = "render.text_draw";
        source::Call<void>(surface_, 17, font_);
        source::Call<void>(surface_, 19, 255, 255, 255, alpha);
        source::Call<void>(surface_, 20, x - width / 2, y);
        source::Call<void>(surface_, 22, text, static_cast<int>(wcslen(text)), 0);
    }
    void Render() {
        Operation context("render.conditions");
        if (!active_) { renderState = "inactive"; return; }
        Subscribe();
        if (!source::Call<bool>(engine_, 26)) { renderState = "not_in_game"; feedback_.Reset(); return; }
        if (!source::Call<bool>(engine_, 27)) { renderState = "not_connected"; feedback_.Reset(); return; }
        if (source::Call<bool>(engine_, 11)) { renderState = "console_visible"; return; }
        if (source::Call<bool>(engineVgui_, 2)) { renderState = "game_ui_visible"; return; }
        DWORD foreground{}; GetWindowThreadProcessId(GetForegroundWindow(), &foreground);
        if (foreground != GetCurrentProcessId()) { renderState = "background"; return; }
        auto now = GetTickCount64();
        if (!feedback_.visible) { animation_.Reset(); renderState = "no_accepted_kill"; return; }
        if (now < feedback_.last || now - feedback_.last > 1800) { animation_.Reset(); renderState = "effect_expired"; return; }
        if (!paintLogged_) { Log("Feedback draw reached the in-game HUD panel."); paintLogged_ = true; }
        for (size_t i = 0; i < textures_.size(); ++i) if (textures_[i] < 0) {
            currentOperation = "render.create_texture"; renderState = "creating_textures";
            textures_[i] = source::CreateTexture(surface_);
            if (textures_[i] < 0) {
                char line[128]{}; std::snprintf(line, sizeof(line), "TEXTURE_CREATION_FAILED image=%u id=%d", static_cast<unsigned>(i), textures_[i]);
                Log(line); active_ = false; phase = "error"; failure = "texture_creation_failed"; Report(true); return;
            }
            currentOperation = "render.upload_texture";
            source::UploadTexture(surface_, textures_[i], images_[i].pixels.data(), images_[i].width, images_[i].height);
            char line[128]{}; std::snprintf(line, sizeof(line), "TEXTURE image=%u id=%d size=%dx%d", static_cast<unsigned>(i), textures_[i], images_[i].width, images_[i].height);
            Log(line, false);
        }
        currentOperation = "render.screen_size";
        int width{}, height{}; source::ScreenSize(engine_, width, height);
        screenWidth = width; screenHeight = height;
        if (width <= 0 || height <= 0) { renderState = "invalid_screen_size"; return; }
        float scale = std::clamp(height / 1080.0f, 0.5f, 3.0f);
        int textHeight = static_cast<int>(22 * scale);
        currentOperation = "render.create_font";
        if (!font_) font_ = source::Call<unsigned long>(surface_, 63);
        if (textHeight != fontHeight_) {
            currentOperation = "render.font_glyphs";
            bool ready = source::Call<bool>(surface_, 64, font_, "Microsoft YaHei", textHeight, 600, 0, 0, 0x210, 0, 0);
            char line[128]{}; std::snprintf(line, sizeof(line), "FONT id=%lu height=%d glyphs_ready=%d", font_, textHeight, ready);
            Log(line, false);
            fontHeight_ = textHeight;
        }
        int alpha = static_cast<int>(255 * std::clamp((1800.0f - static_cast<float>(now - feedback_.last)) / 500.0f, 0.0f, 1.0f));
        int center = width / 2, y = static_cast<int>(height * 0.62f);
        auto frame = animation_.Update(feedback_, acceptedKills, now);
        currentOperation = "render.draw_icons"; renderState = "drawing";
        for (int i = frame.count - 1; i >= 0; --i) {
            const auto& pose = frame.skulls[i];
            if (pose.ringAlpha > 0) {
                int radius = static_cast<int>(pose.ringSize * scale / 2);
                int ringX = center + static_cast<int>(pose.ringX * scale);
                source::Call<void>(surface_, 11, 255, pose.headshot ? 196 : 255, pose.headshot ? 72 : 255, static_cast<int>(alpha * pose.ringAlpha));
                source::Call<void>(surface_, 29, textures_[2]);
                source::Call<void>(surface_, 31, ringX - radius, y - radius, ringX + radius, y + radius);
            }
            int box = static_cast<int>(27 * scale * pose.scale);
            size_t type = pose.headshot ? 1 : 0;
            int h = box, w = static_cast<int>(box * static_cast<float>(images_[type].width) / images_[type].height);
            int x = center + static_cast<int>(pose.x * scale), iconY = y + static_cast<int>(pose.y * scale);
            source::Call<void>(surface_, 11, 255, 255, 255, static_cast<int>(alpha * pose.alpha));
            source::Call<void>(surface_, 29, textures_[type]);
            source::Call<void>(surface_, 31, x - w / 2, iconY - h / 2, x + w / 2, iconY + h / 2);
        }
        const auto& latest = feedback_.kills.back();
        wchar_t caption[128]{};
        swprintf_s(caption, L"%s · %s", latest.headshot ? L"爆头击杀" : L"击杀", ember::Label(latest.kind));
        DrawText(caption, center, y + static_cast<int>(26 * scale), alpha);
        ++hudDraws;
        renderState = "drawn";
        if (hudDraws == 1) {
            char line[160]{}; std::snprintf(line, sizeof(line), "HUD_FIRST_DRAW_COMPLETE textures=%d,%d screen=%dx%d", textures_[0], textures_[1], width, height);
            Log(line, false); Report(true);
        }
    }
    bool InitializeOnOwnerThread() {
        Operation context("initialize.com");
        // WIC and XAudio2 are initialized on this same owner thread. Retain our
        // COM reference for the module's game-process lifetime.
        HRESULT com = feedbackEnabled ? CoInitializeEx(nullptr, COINIT_MULTITHREADED) : S_OK;
        if (FAILED(com) && com != RPC_E_CHANGED_MODE) { failure = "com_initialization_failed"; return false; }
        ownsCom_ = feedbackEnabled && SUCCEEDED(com);
        phase = "receive_tables"; Report(true);
        Log("Initializing client receive tables.");
        currentOperation = "initialize.receive_tables";
        int classes = 0;
        for (auto klass = source::ClientClasses(client_); klass && ++classes < 2048; klass = klass->next) {
            if (klass->name && !std::strcmp(klass->name, "CTerrorPlayer")) {
                teamOffset_ = Property(klass->table, "m_iTeamNum"); zombieOffset_ = Property(klass->table, "m_zombieClass");
                if (autoPistolEnabled) {
                    activeWeaponOffset_ = Property(klass->table, "m_hActiveWeapon");
                    lifeOffset_ = Property(klass->table, "m_lifeState");
                }
            }
            if (autoPistolEnabled && klass->name && !std::strcmp(klass->name, "CPistol")) clipOffset_ = Property(klass->table, "m_iClip1");
            if (teamOffset_ >= 0 && (!autoPistolEnabled || clipOffset_ >= 0)) break;
        }
        debugTeamOffset = teamOffset_; debugZombieOffset = zombieOffset_;
        char offsets[192]{}; std::snprintf(offsets, sizeof(offsets), "RECEIVE_TABLES classes=%d team_offset=%d zombie_offset=%d", classes, teamOffset_, zombieOffset_);
        Log(offsets, false);
        if (teamOffset_ < 0 || teamOffset_ > 65536 || zombieOffset_ < 0 || zombieOffset_ > 65536) { failure = "receive_properties_unavailable"; return false; }
        if (autoPistolEnabled) {
            char pistol[192]{}; std::snprintf(pistol, sizeof(pistol), "PISTOL_PROPERTIES active_weapon=%d life=%d clip=%d", activeWeaponOffset_, lifeOffset_, clipOffset_);
            Log(pistol, false);
            if (activeWeaponOffset_ < 0 || activeWeaponOffset_ > 65536 || lifeOffset_ < 0 || lifeOffset_ > 65536 || clipOffset_ < 0 || clipOffset_ > 65536) {
                failure = "pistol_receive_properties_unavailable"; return false;
            }
            if (!pistolInput.Start(ownModule)) { failure = "pistol_input_thread_failed"; return false; }
        }
        if (feedbackEnabled) {
        phase = "images"; Report(true);
        Log("Receive tables ready; decoding feedback images.");
        currentOperation = "initialize.images";
        if (!ReadImage(moduleDirectory / L"assets/skull.png", images_[0]) || !ReadImage(moduleDirectory / L"assets/headshot.png", images_[1])) {
            failure = "image_decode_failed"; return false;
        }
        images_[2] = RingImage();
        feedback_.all = scope == 0;
        phase = "audio"; Report(true);
        Log("Feedback images ready; initializing audio.");
        currentOperation = "initialize.audio";
        audioReady = audio_.Init();
        if (!audioReady) Log("Audio initialization failed; visual feedback remains available.");
        }
        active_ = true;
        phase = "initialized";
        if (feedbackEnabled) {
            phase = "events"; Report(true);
            Log("Registering client event listeners.");
            Subscribe();
        }
        Log("Client owner-thread initialization complete. No server-plugin registration or launch security flags used.");
        Report(true);
        return true;
    }
public:
    bool Start(const wchar_t* session) {
        Operation context("start.configure");
        if (paintSlot_ || pending_.load()) return false;
        wchar_t path[32768]{};
        if (!GetModuleFileNameW(ownModule, path, static_cast<DWORD>(std::size(path)))) return false;
        moduleDirectory = std::filesystem::path(path).parent_path();
        stateDirectory = moduleDirectory.parent_path() / L"data/Feedback";
        std::filesystem::create_directories(stateDirectory);
        sessionId.clear();
        for (int i = 0; i < 32; ++i) sessionId += static_cast<char>(session[i]);
        auto dos = reinterpret_cast<IMAGE_DOS_HEADER*>(ownModule);
        ownImageSize = reinterpret_cast<IMAGE_NT_HEADERS*>(reinterpret_cast<unsigned char*>(ownModule) + dos->e_lfanew)->OptionalHeader.SizeOfImage;
        exceptionLog = CreateFileW((stateDirectory / ("session-" + sessionId + "-exceptions.log")).c_str(), FILE_APPEND_DATA,
            FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_WRITE_THROUGH, nullptr);
        char build[192]{}; std::snprintf(build, sizeof(build), "DEBUG_BUILD version=%s dll_base=%p exception_log_ready=%d", debugVersion, static_cast<void*>(ownModule), exceptionLog != INVALID_HANDLE_VALUE);
        Log(build, false);
        Report(true);
        const auto config = stateDirectory / L"settings.ini";
        wchar_t expected[64]{};
        GetPrivateProfileStringW(L"feedback", L"session", L"", expected, static_cast<DWORD>(std::size(expected)), config.c_str());
        if (GetPrivateProfileIntW(L"feedback", L"enabled", 0, config.c_str()) != 1 || std::wcscmp(expected, session)) { failure = "launch_session_mismatch"; phase = "error"; Report(true); return false; }
        scope = GetPrivateProfileIntW(L"feedback", L"scope", 1, config.c_str()) == 0 ? 0 : 1;
        feedbackEnabled = GetPrivateProfileIntW(L"feedback", L"kill_feedback", 1, config.c_str()) == 1;
        autoPistolEnabled = GetPrivateProfileIntW(L"feedback", L"auto_pistol", 0, config.c_str()) == 1;
        Log("Our client DLL loaded; session and game binaries are being verified.");
        currentOperation = "start.binary_hashes";
        if (!SupportedModules()) { failure = "unsupported_game_binaries"; phase = "error"; Report(true); return false; }
        currentOperation = "start.interfaces";
        engine_ = Interface(L"engine.dll", "VEngineClient013");
        events_ = Interface(L"engine.dll", "GAMEEVENTSMANAGER002");
        engineVgui_ = Interface(L"engine.dll", "VEngineVGui001");
        entities_ = Interface(L"client.dll", "VClientEntityList003");
        client_ = Interface(L"client.dll", "VClient016");
        panel_ = Interface(L"vgui2.dll", "VGUI_Panel009");
        surface_ = Interface(L"vguimatsurface.dll", "VGUI_Surface031");
        if (!engine_ || !events_ || !engineVgui_ || !entities_ || !client_ || !panel_ || !surface_) { failure = "source_interface_unavailable"; phase = "error"; Report(true); return false; }
        paintSlot_ = &(*reinterpret_cast<void***>(panel_))[41];
        originalPaint_ = reinterpret_cast<Paint>(*paintSlot_);
        char paint[192]{}; std::snprintf(paint, sizeof(paint), "PAINT_HOOK entry=%p original=%p replacement=%p", static_cast<void*>(paintSlot_), reinterpret_cast<void*>(originalPaint_), reinterpret_cast<void*>(&PaintHook));
        Log(paint, false);
        DWORD protection{};
        if (!VirtualProtect(paintSlot_, sizeof(void*), PAGE_READWRITE, &protection)) { paintSlot_ = nullptr; failure = "paint_entry_unavailable"; phase = "error"; Report(true); return false; }
        // All Source API work is deferred to the game's owner/paint thread.
        pending_.store(true, std::memory_order_release);
        InterlockedExchangePointer(paintSlot_, reinterpret_cast<void*>(&PaintHook));
        DWORD ignored{}; VirtualProtect(paintSlot_, sizeof(void*), protection, &ignored);
        return true;
    }
    void Stop() {
        active_ = false;
        pistolInput.Stop();
        if (events_) source::Call<void>(events_, 5, static_cast<source::Listener*>(this));
        if (paintSlot_) {
            DWORD protection{};
            if (VirtualProtect(paintSlot_, sizeof(void*), PAGE_READWRITE, &protection)) {
                InterlockedCompareExchangePointer(paintSlot_, reinterpret_cast<void*>(originalPaint_), reinterpret_cast<void*>(&PaintHook));
                DWORD ignored{}; VirtualProtect(paintSlot_, sizeof(void*), protection, &ignored);
            }
            paintSlot_ = nullptr;
        }
        for (auto& texture : textures_) if (texture >= 0) { source::DeleteTexture(surface_, texture); texture = -1; }
        audio_.Stop(); feedback_.Reset();
        if (ownsCom_) { CoUninitialize(); ownsCom_ = false; }
        Log("Unloaded: listener removed, paint entry restored, audio voices released.");
    }
    void HandleGameEvent(source::GameEvent* event) {
        Operation context("event.is_in_game");
        if (!active_ || !feedbackEnabled || !event || !source::Call<bool>(engine_, 26)) return;
        currentOperation = "event.get_name";
        const char* name = event->GetName();
        bool player = !std::strcmp(name, "player_death"), common = !std::strcmp(name, "infected_death"), witch = !std::strcmp(name, "witch_killed");
        if (player || common || witch) {
            ++deathEvents;
            lastEvent = {}; lastDecision = "pending";
            lastEvent.name = player ? "player_death" : (common ? "infected_death" : "witch_killed");
            currentOperation = "event.raw_fields";
            // -1 makes missing fields distinguishable from a real zero. Only
            // fields used by the existing decision path are inspected.
            lastEvent.attackerRaw = event->GetInt(witch ? "userid" : "attacker", -1);
            if (common) lastEvent.infectedId = event->GetInt("infected_id", -1);
            else if (witch) { lastEvent.userId = lastEvent.attackerRaw; lastEvent.witchId = event->GetInt("witchid", -1); }
            else { lastEvent.userId = event->GetInt("userid", -1); lastEvent.entityId = event->GetInt("entityid", -1); }
        }
        currentOperation = "event.get_local_player";
        int local = source::Call<int>(engine_, 12);
        if (!player && !common && !witch) {
            if (std::strcmp(name, "player_team") || source::Call<int>(engine_, 9, event->GetInt("userid")) == local) feedback_.Reset();
            char line[128]{}; std::snprintf(line, sizeof(line), "RESET event=%.48s local=%d", name, local); Log(line, false);
            return;
        }
        auto& input = lastEvent.input;
        input.local = local;
        currentOperation = "event.abort_field";
        if (player && event->GetBool("abort")) { TraceDeath(ember::Decision::aborted); return; }
        int uid = lastEvent.attackerRaw;
        if (uid <= 0) { TraceDeath(ember::Decision::missingAttacker); return; }
        currentOperation = "event.player_for_attacker";
        input.attacker = source::Call<int>(engine_, 9, uid);
        input.localTeam = Team(local, &lastEvent.localEntity);
        currentOperation = "event.headshot_field";
        input.headshot = event->GetBool("headshot");
        currentOperation = "event.victim_identity";
        if (common) { input.kind = ember::Kind::common; input.identity = event->GetInt("infected_id") & 0xffff; }
        else if (witch) { input.kind = ember::Kind::witch; input.identity = event->GetInt("witchid"); }
        else {
            input.kind = Victim(event);
            // Use one canonical death event per kind. Some servers also emit
            // player_death for non-player entities; do not count their later
            // infected_death/witch_killed a second time with a different ID.
            if (!ember::IsPlayerInfected(input.kind)) { TraceDeath(input.kind == ember::Kind::none ? ember::Decision::unknownVictim : ember::Decision::noncanonicalVictim); return; }
        }
        currentOperation = "event.feedback_policy";
        ember::Decision decision{};
        if (feedback_.Accept(input, GetTickCount64(), &decision)) {
            ++acceptedKills;
            TraceDeath(decision);
            DWORD foreground{}; GetWindowThreadProcessId(GetForegroundWindow(), &foreground);
            if (foreground == GetCurrentProcessId()) {
                ++audioRequests;
                auto result = audio_.Play(input.headshot);
                if (SUCCEEDED(result)) ++audioStarts;
                char line[128]{}; std::snprintf(line, sizeof(line), "AUDIO result=0x%08lX requested=%llu started=%llu", static_cast<unsigned long>(result), audioRequests, audioStarts); Log(line, false);
            }
        }
        else TraceDeath(decision);
    }
    void FireGameEvent(source::GameEvent* event) override {
        __try { HandleGameEvent(event); }
        __except (RecordException(GetExceptionInformation())) {} // Always continues normal exception search.
    }
    void PaintPanel(void* self, unsigned int panel, bool force, bool allow) {
        Operation context("panel.original_paint");
        originalPaint_(self, panel, force, allow);
        currentOperation = "panel.get_name";
        const char* name = source::Call<const char*>(panel_, 36, panel);
        if (name && !std::strcmp(name, "MatSystemTopPanel")) {
            ++paintFrames;
            try {
                if (pending_.exchange(false, std::memory_order_acquire) && !InitializeOnOwnerThread()) { phase = "error"; Report(true); }
                UpdatePistol();
                if (feedbackEnabled) Render();
                currentOperation = "status.write";
                Report();
                Heartbeat();
            }
            catch (...) { active_ = false; phase = "error"; failure = "native_exception"; Report(true); Log("Client feedback stopped after an exception."); }
        }
    }
};
Plugin plugin;
void __fastcall PaintHook(void* self, void*, unsigned int panel, bool force, bool allow) {
    __try { plugin.PaintPanel(self, panel, force, allow); }
    __except (RecordException(GetExceptionInformation())) {}
}
DWORD StartClient(void* argument) {
    const auto session = static_cast<const wchar_t*>(argument);
    if (!session || wcsnlen_s(session, 33) != 32) return 2;
    for (int i = 0; i < 32; ++i) if (!iswxdigit(session[i])) return 2;
    try { return plugin.Start(session) ? 1 : 3; }
    catch (...) { return 4; }
}
}

extern "C" __declspec(dllexport) DWORD WINAPI EmberFeedback_Start(void* argument) {
    __try { return StartClient(argument); }
    __except (RecordException(GetExceptionInformation())) { return 4; }
}
BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) { ownModule = module; DisableThreadLibraryCalls(module); }
    return TRUE;
}
