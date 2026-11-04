// Copyright 2026 EmberKit contributors. SPDX-License-Identifier: Apache-2.0
// Isolated loader test fixture. This executable/DLL never loads a game or Steam.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <filesystem>
#include <fstream>
#include <string>

#ifdef EMBER_FIXTURE_DLL
HMODULE fixtureModule;
extern "C" __declspec(dllexport) DWORD WINAPI EmberFeedback_Start(void* argument) {
    const auto session = static_cast<const wchar_t*>(argument);
    if (!session || wcslen(session) != 32) return 2;
    wchar_t path[32768]{}; GetModuleFileNameW(fixtureModule, path, static_cast<DWORD>(std::size(path)));
    std::ofstream receipt(std::filesystem::path(path).parent_path() / L"loader-receipt.txt", std::ios::trunc);
    for (int i = 0; i < 32; ++i) receipt << static_cast<char>(session[i]);
    receipt << " " << GetCurrentProcessId(); receipt.flush();
    return receipt.good() ? 1 : 3;
}
BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, LPVOID) { if (reason == DLL_PROCESS_ATTACH) fixtureModule = module; return TRUE; }
#else
int wmain() {
    std::wstring name = L"Local\\EmberFeedbackFixture.Exit." + std::to_wstring(GetCurrentProcessId());
    HANDLE stop = CreateEventW(nullptr, TRUE, FALSE, name.c_str());
    if (!stop) return 2;
    WaitForSingleObject(stop, 30000); CloseHandle(stop);
    return 0;
}
#endif
