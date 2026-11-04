// Copyright 2026 EmberKit contributors. SPDX-License-Identifier: Apache-2.0
// x86 client-module loader. No server plugin command or security-setting change.
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <tlhelp32.h>
#include <filesystem>
#include <cstdint>
#include <iostream>
#include <stdexcept>
#include <string>

namespace ember_loader {
struct Handle {
    HANDLE value{};
    explicit Handle(HANDLE handle) : value(handle) {}
    ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
    Handle(const Handle&) = delete;
    operator HANDLE() const { return value; }
};
[[noreturn]] inline void Fail(const char* operation, DWORD code = GetLastError()) {
    throw std::runtime_error(std::string(operation) + "; win32=" + std::to_string(code));
}
inline std::wstring FullPath(const std::wstring& path) {
    wchar_t result[32768]{};
    DWORD size = GetFullPathNameW(path.c_str(), static_cast<DWORD>(std::size(result)), result, nullptr);
    if (!size || size >= std::size(result)) Fail("absolute path");
    return result;
}
inline bool SamePath(const std::wstring& a, const std::wstring& b) { return _wcsicmp(FullPath(a).c_str(), FullPath(b).c_str()) == 0; }

inline std::uintptr_t RemoteModule(DWORD pid, const std::wstring& name, bool fullPath) {
    // Snapshot enumeration can briefly fail while the target loader updates its list.
    for (int attempt = 0; attempt < 8; ++attempt) {
        Handle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid));
        if (snapshot.value == INVALID_HANDLE_VALUE) {
            if (GetLastError() == ERROR_BAD_LENGTH) { Sleep(25); continue; }
            Fail("module snapshot");
        }
        MODULEENTRY32W entry{}; entry.dwSize = sizeof(entry);
        if (Module32FirstW(snapshot, &entry)) do {
            bool matches = fullPath ? SamePath(entry.szExePath, name) : _wcsicmp(entry.szModule, name.c_str()) == 0;
            if (matches) return reinterpret_cast<std::uintptr_t>(entry.modBaseAddr);
        } while (Module32NextW(snapshot, &entry));
        return 0;
    }
    Fail("module snapshot remained busy", ERROR_BAD_LENGTH);
}
inline DWORD ExportRva(const std::filesystem::path& dll, const char* name) {
    // Map OUR module only for export lookup: no DllMain, static initializer or imports execute.
    HMODULE local = LoadLibraryExW(dll.c_str(), nullptr, DONT_RESOLVE_DLL_REFERENCES);
    if (!local) Fail("map module exports");
    FARPROC entry = GetProcAddress(local, name);
    DWORD result = entry ? static_cast<DWORD>(reinterpret_cast<std::uintptr_t>(entry) - reinterpret_cast<std::uintptr_t>(local)) : 0;
    FreeLibrary(local);
    if (!result) Fail("missing EmberFeedback_Start export", ERROR_PROC_NOT_FOUND);
    return result;
}
inline DWORD Invoke(HANDLE process, std::uintptr_t entry, const std::wstring& argument) {
    SIZE_T bytes = (argument.size() + 1) * sizeof(wchar_t);
    void* memory = VirtualAllocEx(process, nullptr, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!memory) Fail("allocate DLL path/session");
    SIZE_T written{};
    if (!WriteProcessMemory(process, memory, argument.c_str(), bytes, &written) || written != bytes) {
        DWORD error = GetLastError(); VirtualFreeEx(process, memory, 0, MEM_RELEASE); Fail("write DLL path/session", error);
    }
    Handle thread(CreateRemoteThread(process, nullptr, 0, reinterpret_cast<LPTHREAD_START_ROUTINE>(entry), memory, 0, nullptr));
    if (!thread.value) { DWORD error = GetLastError(); VirtualFreeEx(process, memory, 0, MEM_RELEASE); Fail("start module thread", error); }
    DWORD wait = WaitForSingleObject(thread, 15000);
    if (wait != WAIT_OBJECT_0) {
        // A running thread can still read its argument. Do not free it or kill the game.
        Fail("module thread not completed; no retry or termination", wait == WAIT_TIMEOUT ? WAIT_TIMEOUT : GetLastError());
    }
    DWORD result{};
    bool read = GetExitCodeThread(thread, &result) != FALSE;
    VirtualFreeEx(process, memory, 0, MEM_RELEASE);
    if (!read) Fail("module thread result");
    return result;
}
inline void Attach(DWORD pid, const std::filesystem::path& gameExe, const std::filesystem::path& dll, const std::wstring& session) {
    static_assert(sizeof(void*) == 4, "The helper must be x86, matching L4D2.");
    if (session.size() != 32 || session.find_first_not_of(L"0123456789abcdefABCDEF") != std::wstring::npos)
        Fail("invalid session", ERROR_INVALID_PARAMETER);
    if (dll.filename() != L"EmberFeedback.dll") Fail("unexpected module filename", ERROR_INVALID_NAME);
    Handle process(OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ | SYNCHRONIZE, FALSE, pid));
    if (!process.value) Fail("open requested game process");
    wchar_t image[32768]{}; DWORD size = static_cast<DWORD>(std::size(image));
    if (!QueryFullProcessImageNameW(process, 0, image, &size) || !SamePath(image, gameExe.wstring())) Fail("game process path mismatch", ERROR_INVALID_PARAMETER);
    if (WaitForSingleObject(process, 0) != WAIT_TIMEOUT) Fail("game process already exited", ERROR_PROCESS_ABORTED);
    if (RemoteModule(pid, dll.wstring(), true)) Fail("module already loaded; no duplicate initialization", ERROR_ALREADY_EXISTS);
    DWORD startRva = ExportRva(dll, "EmberFeedback_Start");
    FARPROC load = GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW");
    HMODULE owner{};
    if (!load || !GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        reinterpret_cast<LPCWSTR>(load), &owner)) Fail("resolve system loader");
    wchar_t ownerPath[32768]{};
    if (!GetModuleFileNameW(owner, ownerPath, static_cast<DWORD>(std::size(ownerPath)))) Fail("resolve system loader module");
    auto remoteOwner = RemoteModule(pid, std::filesystem::path(ownerPath).filename().wstring(), false);
    if (!remoteOwner) Fail("system loader absent in target", ERROR_MOD_NOT_FOUND);
    auto remoteLoad = remoteOwner + reinterpret_cast<std::uintptr_t>(load) - reinterpret_cast<std::uintptr_t>(owner);
    if (!Invoke(process, remoteLoad, dll.wstring())) Fail("LoadLibraryW returned zero; module/dependency loading failed", ERROR_DLL_INIT_FAILED);
    auto remoteDll = RemoteModule(pid, dll.wstring(), true);
    if (!remoteDll) Fail("loaded module path not observed", ERROR_MOD_NOT_FOUND);
    DWORD result = Invoke(process, remoteDll + startRva, session);
    if (result != 1) Fail("client start rejected; see native session status", result);
}
}

#ifndef EMBER_LOADER_TESTS
int wmain(int argc, wchar_t** argv) {
    try {
        if (argc != 5) throw std::runtime_error("Usage: EmberFeedback.Loader.exe <pid> <absolute-left4dead2.exe> <absolute-EmberFeedback.dll> <session>");
        wchar_t* end{}; unsigned long pid = wcstoul(argv[1], &end, 10);
        if (!pid || !end || *end) throw std::runtime_error("Invalid PID");
        std::filesystem::path game(ember_loader::FullPath(argv[2])), dll(ember_loader::FullPath(argv[3]));
        if (_wcsicmp(game.filename().c_str(), L"left4dead2.exe") != 0) throw std::runtime_error("Only the requested L4D2 executable is accepted");
        ember_loader::Attach(pid, game, dll, argv[4]);
        std::cout << "CLIENT_MODULE_STARTED pid=" << pid << "; owner-thread initialization still requires its status receipt.\n";
        return 0;
    } catch (const std::exception& error) { std::cerr << "CLIENT_MODULE_FAILED " << error.what() << "\n"; return 1; }
}
#endif
