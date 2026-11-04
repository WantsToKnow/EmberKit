// Copyright 2026 EmberKit contributors. SPDX-License-Identifier: Apache-2.0
#pragma once
#include <cstddef>

// ABI facts, not a copy of the reference mod. References:
// https://github.com/alliedmodders/hl2sdk/tree/l4d2/public
// igameevents.h, engine/iserverplugin.h, dt_recv.h, client_class.h,
// cdll_int.h, icliententitylist.h, iclientnetworkable.h, vgui/IPanel.h, vgui/ISurface.h.
// VClient016, Surface031 and Panel009 slots were checked against the pinned game binaries.
// MSVC reverses overloaded DrawSetColor/DrawSetTextColor entries in these interfaces.
namespace source {
using Factory = void* (__cdecl*)(const char*, int*);
template<class Return, class... Args> Return Call(void* object, int slot, Args... args) {
    using Method = Return(__thiscall*)(void*, Args...);
    return reinterpret_cast<Method>((*reinterpret_cast<void***>(object))[slot])(object, args...);
}
// Reference parameters must not be deduced by value by Call().
inline void ScreenSize(void* engine, int& width, int& height) {
    using Method = void(__thiscall*)(void*, int&, int&);
    reinterpret_cast<Method>((*reinterpret_cast<void***>(engine))[5])(engine, width, height);
}
inline void TextSize(void* surface, unsigned long font, const wchar_t* text, int& width, int& height) {
    using Method = void(__thiscall*)(void*, unsigned long, const wchar_t*, int&, int&);
    reinterpret_cast<Method>((*reinterpret_cast<void***>(surface))[72])(surface, font, text, width, height);
}
inline void UploadTexture(void* surface, int texture, const unsigned char* rgba, int width, int height) {
    // Surface031 slot 28 is four arguments (ret 0x10), unlike the old
    // public Surface030 header's six-argument hardwareFilter/forceReload form.
    using Method = void(__thiscall*)(void*, int, const unsigned char*, int, int);
    reinterpret_cast<Method>((*reinterpret_cast<void***>(surface))[28])(surface, texture, rgba, width, height);
}
inline int CreateTexture(void* surface) {
    // Surface031 added DeleteTextureByID before creation. In this pinned
    // build slot 34 (+0x8450) forwards to TextureDictionary::CreateTexture.
    // Slot 33 (+0x8490) DELETES an ID and returns bool, not a new texture ID.
    return Call<int>(surface, 34, true);
}
// The pinned delete implementation returns false even after removal; callers
// must not interpret that boolean as proof that a texture is still present.
inline bool DeleteTexture(void* surface, int texture) { return Call<bool>(surface, 33, texture); }
struct GameEvent {
    virtual ~GameEvent() = default;
    virtual const char* GetName() const = 0;
    virtual bool IsReliable() const = 0;
    virtual bool IsLocal() const = 0;
    virtual bool IsEmpty(const char* key = nullptr) = 0;
    virtual bool GetBool(const char* key, bool fallback = false) = 0;
    virtual int GetInt(const char* key, int fallback = 0) = 0;
    virtual unsigned long long GetUint64(const char* key, unsigned long long fallback = 0) = 0;
    virtual float GetFloat(const char* key, float fallback = 0) = 0;
    virtual const char* GetString(const char* key, const char* fallback = "") = 0;
};
struct Listener {
    virtual ~Listener() = default;
    virtual void FireGameEvent(GameEvent* event) = 0;
    virtual int GetEventDebugID() { return 42; }
};
struct RecvTable;
struct RecvProp {
    const char* name;
    int type, flags, stringSize;
    bool insideArray;
    const void* extra;
    RecvProp* array;
    void* arrayLengthProxy;
    void* proxy;
    void* tableProxy;
    RecvTable* table;
    int offset, stride, count;
    const char* parentArray;
};
struct RecvTable { RecvProp* props; int count; void* decoder; const char* name; bool initialized, inMainList; };
struct ClientClass { void* create; void* createEvent; const char* name; RecvTable* table; ClientClass* next; int id; };
static_assert(sizeof(void*) == 4, "The L4D2 module must be compiled as Win32, even on x64 Windows.");
static_assert(sizeof(RecvProp) == 60 && offsetof(RecvProp, offset) == 44);
static_assert(offsetof(ClientClass, table) == 12);

inline ClientClass* ClientClasses(void* client) {
    // This pinned VClient016 uses slot 7, unlike the older public SDK's slot 6.
    // client.dll+0xA8A40 is the list getter; slot 8 is HudVidInit (returns 1).
    // The 2026-11-03 crash dumps confirm that treating slot 8 as a pointer
    // caused an access violation at ClientClass::name (1 + 8 == 0x9).
    return Call<ClientClass*>(client, 7);
}
inline ClientClass* NetworkClass(void* networkable) {
    // The pinned game's IClientNetworkable getter is slot 1, not the old
    // public SDK's slot 2. Verified against the 2026-11-04 Smoker crash:
    // vtable client.dll+0x60DA34, getter +0x318710 -> ClientClass "Smoker".
    // C_TerrorPlayer's +0x5B63A4 table has the same slot layout.
    return Call<ClientClass*>(networkable, 1);
}

struct GameFile { const wchar_t* name; const char* hash; };
inline constexpr GameFile pinned[] = {
    { L"engine.dll", "8a6d468059394bf4cfce32f11dcba12ef45de88da1373084d5a2916d0bf7ba86" },
    { L"client.dll", "c0a8e1e88f7312db11bcfd4b0ec574d224a34b2d9a615d6088b28335eeb4af1f" },
    { L"vguimatsurface.dll", "3f2e2d74b3934a482caa8c3d5aa8d1157e04d719185b019ffaa1f9f7b4bb1d58" },
    { L"vgui2.dll", "3188733e5cb87a2c72e616160e7c5298da065dcaa3248021f4abd5694eb701c1" }
};
}
