# How Mithya Works

## Overview

Mithya is a Windows DLL injection tool with several independent features:

1. **Fix Focus Loss** — Prevents games/apps from pausing or muting when they lose focus
2. **Bypass Screenshot** — Removes screenshot protection so capture tools (Snipping Tool, OCR, etc.) can see the window
3. **Exclude from Capture** — Forces `WDA_EXCLUDEFROMCAPTURE` so the app appears black/blank in any capture or screen share
4. **Enable Text Copy** — Lets you copy text from apps that block it

---

## Architecture

```
NoFocusLossGUI.exe  (WPF, .NET 4.8)
    └── SharpestInjector  (C# library)
            └── NoFocusLoss.dll / NoFocusLoss64.dll  (C++ payload)
                    └── MinHook  (API hooking library)
```

---

## How Injection Works

### Step 1 — Signal via Named Events (Before Injection)

The GUI creates Windows Named Events in the Local namespace before injecting:

| Feature | Named Event |
|---|---|
| Fix Focus Loss | `Local\NFL_Focus_{PID}` |
| Bypass Screenshot | `Local\NFL_Bypass_{PID}` |
| Exclude from Capture | `Local\NFL_Privacy_{PID}` |
| Enable Text Copy | `Local\NFL_TextCopy_{PID}` |

These are signalled (set to `true`) so the DLL can open and read them.

### Step 2 — LoadLibrary Injection (SharpestInjector)

SharpestInjector performs classic **LoadLibrary injection**:

1. Opens the target process with `OpenProcess` (needs admin for some processes)
2. Allocates memory inside target with `VirtualAllocEx`
3. Writes the DLL path into that memory with `WriteProcessMemory`
4. Calls `CreateRemoteThread` pointing at `LoadLibraryW` with the path as argument
5. The target process loads the DLL as if it loaded it natively

### Step 3 — DLL Initializes (DllMain → InitThread)

When the DLL loads inside the target:

```
DllMain (DLL_PROCESS_ATTACH)
    └── MH_Initialize()          ← MinHook ready
    └── CreateThread(InitThread) ← Spawned separately to avoid loader-lock
            └── Sleep(100ms)     ← Wait for LoadLibrary to fully return
            └── OpenEvent("Local\NFL_Focus_{PID}")   → SetupFocusFix()
            └── OpenEvent("Local\NFL_Bypass_{PID}")  → StartScreenshotBypass()
            └── OpenEvent("Local\NFL_Privacy_{PID}") → StartPrivacyProtection()
```

> **Why a separate thread?** Doing complex operations directly in `DllMain` can deadlock because Windows holds the loader lock during DLL loading. Spawning a thread and waiting 100ms avoids this safely.

---

## Feature 1: Fix Focus Loss

### What it does

Prevents Windows from telling the app it lost focus, so games/apps continue running normally when you alt-tab.

### How it does it

**A. API Hooking (`GetForegroundWindow` + `SetCursorPos`)**

Using MinHook, the DLL patches the import table entries in memory:

- `GetForegroundWindow` → always returns the app's own window handle (app thinks it's always focused)
- `SetCursorPos` → silently discarded when app is unfocused (prevents mouse snap-back)

**B. Window Procedure Subclassing (`SetWindowLongPtr`)**

Replaces the app's `WndProc` (message handler) with a custom one that intercepts and swallows focus-loss messages:

| Message | Action |
|---|---|
| `WM_NCACTIVATE` (wParam=FALSE) | Return 0 — app never sees "deactivated" |
| `WM_ACTIVATE` (WA_INACTIVE) | Return 0 |
| `WM_ACTIVATEAPP` (wParam=FALSE) | Return 0 |
| `WM_KILLFOCUS` | Return 0 |
| `WM_IME_SETCONTEXT` (wParam=FALSE) | Return 0 |

All other messages pass through to the original WndProc normally.

---

## Feature 2: Bypass Screenshot Protection

### The Problem

Apps call `SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)` which tells the Windows Desktop Window Manager (DWM) to render that window as a black rectangle in any software capture — screenshots, screen recorders, OCR tools, etc.

### How we bypass it

**A. Hook `SetWindowDisplayAffinity`**

Any call the app makes to protect its window is intercepted. Our detour always calls the real function with `WDA_NONE` (0) instead, silently discarding whatever protection level was requested.

**B. Immediate Strip on Injection**

After the hook is installed, we enumerate all windows belonging to the process and call `SetWindowDisplayAffinity(hwnd, WDA_NONE)` on each — including child windows — to remove any protection already set before injection.

**C. Background Keep-Alive Thread**

Some apps periodically re-apply protection. A background thread runs every 250ms and re-strips all windows in a loop for the lifetime of the injection.

```
Background Thread (250ms loop)
    └── EnumWindows (filter by PID)
            └── SetWindowDisplayAffinity(hwnd, WDA_NONE)
            └── EnumChildWindows
                    └── SetWindowDisplayAffinity(child, WDA_NONE)
```

---

## Feature 3: Exclude from Capture (Privacy)

### The Problem

Normally every window you see is also visible to anything that captures the screen — screenshots, screen recorders, and screen-sharing tools (Zoom, Meet, Teams, OBS). There's no built-in UI to hide a single window from captures while still seeing it yourself.

### The Solution

Windows exposes `SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)` — the DWM then renders that window as a black rectangle in *any* capture, while it keeps displaying normally on the real screen. This is exactly what DRM video players use. Mithya simply force-applies it, the mirror of **Feature 2**:

**A. Force the flag on every window**

After injection, `ExcludeAllFromCapture()` enumerates all windows belonging to the process (including children) and sets `WDA_EXCLUDEFROMCAPTURE` on each.

**B. Hook `SetWindowDisplayAffinity`**

Apps that try to clear protection (call with `WDA_NONE`) are intercepted — the detour always re-applies `WDA_EXCLUDEFROMCAPTURE` instead. The same hook serves both features: when privacy is active it forces `WDA_EXCLUDEFROMCAPTURE`, when screenshot-bypass is active it forces `WDA_NONE`, and if both flags were set privacy wins.

**C. Background Keep-Alive Thread**

Some apps periodically reset the affinity. A background thread re-applies `WDA_EXCLUDEFROMCAPTURE` every 250 ms for the lifetime of the injection.

> **Note:** Exclude-from-Capture and Bypass-Screenshot are exact opposites. Pick **one** per process — the GUI moves the process to the *Injected* list after the first injection, so you'd have to unload before switching.

---

## How Named Event IPC Works

The GUI and DLL communicate through the Windows kernel object namespace:

```
GUI Process                           Target Process
    │                                      │
    ├─ CreateEvent("NFL_Focus_{PID}")       │
    ├─ CreateEvent("NFL_Bypass_{PID}")      │
    ├─ CreateEvent("NFL_Privacy_{PID}")     │
    ├─ Inject DLL ───────────────────────► │
    │                                      ├─ InitThread starts
    │                                      ├─ OpenEvent("NFL_Focus_{PID}")  ✓ found
    │                                      ├─ OpenEvent("NFL_Bypass_{PID}") ✓ found
    │                                      ├─ OpenEvent("NFL_Privacy_{PID}") ✓ found
    │                                      ├─ SetupFocusFix()
    │                                      ├─ StartScreenshotBypass()
    │                                      └─ StartPrivacyProtection()
    ├─ Sleep 2000ms
    └─ CloseHandle (events auto-deleted by OS when no handles remain)
```

This approach requires **zero parameter passing** and is 100% reliable. Previous approaches using `CallExport` with parameters failed because `SharpestInjector` passes pointers-to-values (not values directly), causing the DLL to receive garbage.

---

## Risks & Limitations

### ⚠ Anti-Cheat Detection
DLL injection is the same technique used by game cheats. **Never use this in multiplayer games with anti-cheat (VAC, EAC, BattlEye).** You will be banned.

### ⚠ Process Stability
Injecting into the wrong process or a process with unusual architecture can cause crashes. The app filters to only show windowed processes to reduce risk.

### ⚠ Requires Matching Privilege
To inject into an elevated (admin) process, the injector must also run as admin. Run `NoFocusLossGUI.exe` as Administrator if injection fails.

### ⚠ Screenshot Bypass Limitations
- Works against `SetWindowDisplayAffinity` (Win32 API) — the most common method
- Does **not** bypass hardware-level DRM (e.g., HDCP on physical video streams)
- Does **not** work for UWP/sandboxed apps where injection itself fails
- Does **not** bypass kernel-level protection (e.g., games with kernel anti-cheat)

### ⚠ Focus Fix Limitations
- Only affects the main window of the injected process
- Apps using `Windows.Gaming.Input` (vs XInput) may still lose controller input
- Some Unity games use `ClipCursor` instead of `SetCursorPos` — cursor may still snap back

### ⚠ Window Title Bar
While focus fix is active and the app captures the mouse, dragging/minimizing/closing via the title bar may be difficult. Use keyboard shortcuts instead.

---

## Unloading / Cleanup

When you click **Unload**:
1. `DLL_PROCESS_DETACH` fires inside the target
2. The background bypass thread is stopped (`g_bypassActive = 0`, thread joins)
3. The original `WndProc` is restored via `SetWindowLongPtr`
4. All MinHook hooks are disabled and uninitialized
5. The DLL is freed from the process by calling `FreeLibrary`

The target process returns to its original state.

---

## Technology Stack

| Component | Technology |
|---|---|
| GUI | WPF (.NET Framework 4.8), Windows 11 Fluent Design |
| Injector | C# (SharpestInjector), `CreateRemoteThread` + `LoadLibraryW` |
| Payload DLL | C++ (Win32), compiled as both x86 and x64 |
| API Hooking | MinHook (inline hook library by Tsuda Kageyu) |
| IPC | Windows Named Events (`Local\` namespace) |
| Backdrop | DWM Mica material via `DwmSetWindowAttribute` |
