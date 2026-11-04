// Copyright 2026 EmberKit contributors. SPDX-License-Identifier: Apache-2.0
#pragma once
#include <Windows.h>
#include <atomic>
#include "pistol_logic.h"

namespace ember {
class PistolClicker {
    inline static PistolClicker* instance_{};
    HANDLE stop_{}, thread_{};
    HMODULE module_{};
    PistolPulse pulse_;
    std::atomic<unsigned long long> context_{ 0 }, clicks_{ 0 };
    std::atomic<bool> ready_{ false }, active_{ false };
    std::atomic<DWORD> error_{ 0 };

    static bool Foreground() {
        DWORD pid{};
        GetWindowThreadProcessId(GetForegroundWindow(), &pid);
        return pid == GetCurrentProcessId();
    }
    static LRESULT CALLBACK MouseHook(int code, WPARAM message, LPARAM data) {
        if (code == HC_ACTION && instance_ && (message == WM_LBUTTONDOWN || message == WM_LBUTTONUP)) {
            auto event = reinterpret_cast<const MSLLHOOKSTRUCT*>(data);
            instance_->pulse_.Observe((event->flags & LLMHF_INJECTED) != 0, message == WM_LBUTTONDOWN);
        }
        return CallNextHookEx(nullptr, code, message, data);
    }
    static DWORD WINAPI Run(void* argument) {
        auto self = static_cast<PistolClicker*>(argument);
        instance_ = self;
        auto hook = SetWindowsHookExW(WH_MOUSE_LL, MouseHook, self->module_, 0);
        if (!hook) { self->error_ = GetLastError(); return 1; }
        self->ready_ = true;
        // A dedicated message loop keeps the hook responsive during game/map loading.
        while (WaitForSingleObject(self->stop_, 0) == WAIT_TIMEOUT) {
            MSG message{};
            while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
                TranslateMessage(&message); DispatchMessageW(&message);
            }
            auto now = GetTickCount64(), context = self->context_.load(std::memory_order_acquire);
            auto stamp = context >> 2;
            bool gameplay = stamp && now >= stamp && now - stamp <= 250 && (context & 1);
            auto action = self->pulse_.Update(now, Foreground(), gameplay, (context & 2) != 0);
            self->active_ = self->pulse_.Active();
            if (action != MouseAction::none && Foreground()) {
                INPUT input{}; input.type = INPUT_MOUSE;
                input.mi.dwFlags = action == MouseAction::down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP;
                if (SendInput(1, &input, sizeof(input)) != 1) {
                    DWORD error = GetLastError(); self->error_ = error ? error : ERROR_GEN_FAILURE;
                    self->pulse_.Reset(); self->active_ = false;
                }
                else if (action == MouseAction::down && self->active_) ++self->clicks_;
            }
            MsgWaitForMultipleObjects(1, &self->stop_, FALSE, 10, QS_ALLINPUT);
        }
        if (Foreground() && self->pulse_.Update(GetTickCount64(), true, false, false) == MouseAction::up) {
            INPUT input{}; input.type = INPUT_MOUSE; input.mi.dwFlags = MOUSEEVENTF_LEFTUP;
            SendInput(1, &input, sizeof(input));
        }
        UnhookWindowsHookEx(hook);
        self->ready_ = self->active_ = false;
        instance_ = nullptr;
        return 0;
    }
public:
    bool Start(HMODULE module) {
        if (thread_) return true;
        module_ = module;
        stop_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (stop_) thread_ = CreateThread(nullptr, 0, Run, this, 0, nullptr);
        if (!thread_) {
            error_ = GetLastError();
            if (stop_) { CloseHandle(stop_); stop_ = nullptr; }
            return false;
        }
        return true;
    }
    void SetContext(bool gameplay, bool pistol) {
        context_.store((GetTickCount64() << 2) | (gameplay ? 1 : 0) | (pistol ? 2 : 0), std::memory_order_release);
    }
    void Stop() {
        if (!thread_) return;
        SetEvent(stop_);
        if (WaitForSingleObject(thread_, 1000) == WAIT_OBJECT_0) {
            CloseHandle(thread_); CloseHandle(stop_); thread_ = stop_ = nullptr;
        }
    }
    bool Ready() const { return ready_.load(); }
    bool Active() const { return active_.load(); }
    DWORD Error() const { return error_.load(); }
    unsigned long long Clicks() const { return clicks_.load(); }
};
}
