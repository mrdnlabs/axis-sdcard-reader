// AxisSdReader.exe - the entry point of Axis SD Card Reader, run as administrator.
//
// Why it exists: the app needs administrator rights (raw SD card access) but is a .NET program, and the .NET
// runtime acts on environment variables before any of the app's own code runs - CORECLR_ENABLE_PROFILING with
// CORECLR_PROFILER_PATH loads a DLL into the process, DOTNET_DbgEnableMiniDump/DbgMiniDumpName write memory
// dumps to a chosen path, DOTNET_EnableEventPipe writes traces, and so on. A process that Windows elevates gets
// the user's environment, which any program running as that user can change (HKCU\Environment), so starting the
// .NET app elevated directly would let such a program run code with administrator rights.
//
// This launcher is plain native code that none of those variables affect, and it is also the app's .NET host: the
// package has no AxisSdReader.App.exe that could be elevated some other way ("Run as administrator", a compatibility
// flag) with the user's environment. Windows elevates this exe (its manifest asks for administrator rights); it
// replaces its own environment with one built from scratch - a short allow-list, every location taken from Windows
// itself (the kernel, HKLM) rather than from variables - turns on the image-load and link-following protections,
// checks that its folder is one only administrators can change (see "The install folder"), and only then loads the
// runtime (hostfxr.dll) from that folder and runs AxisSdReader.App.dll in this process. No command-line arguments
// are passed on: nothing outside the app chooses what the elevated app opens.
//
// Replacing a running process's environment isn't enough on its own: every C runtime in the process (msvcrt is
// loaded before this code runs, and libVLC reads its environment through it) keeps the copy it took when it
// started. So unless this process already started with exactly the clean environment, it starts itself again
// with that environment (an elevated process's child is elevated too, without another prompt) and leaves.

#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <aclapi.h>
#include <sddl.h>
#include <userenv.h>
#include <stdlib.h>
#include <wchar.h>

#define APP_DLL L"AxisSdReader.App.dll"
#define HOSTFXR_DLL L"hostfxr.dll"
#define TITLE L"Axis SD Card Reader"

// hostfxr's entry point for an app host (the same one .NET's own apphost uses), and its error reporting.
typedef int(__cdecl *hostfxr_main_startupinfo_fn)(const int argc, const wchar_t **argv, const wchar_t *host_path,
                                                 const wchar_t *dotnet_root, const wchar_t *app_path);
typedef void(__cdecl *hostfxr_error_writer_fn)(const wchar_t *message);
typedef hostfxr_error_writer_fn(__cdecl *hostfxr_set_error_writer_fn)(hostfxr_error_writer_fn writer);

// Set in the app's environment so it knows not to relaunch through here again (e.g. when Windows didn't elevate
// this launcher because UAC is off for a standard user). Spoofing it can only stop a relaunch.
#define LAUNCHED_MARKER L"AXISSDREADER_LAUNCHED"

typedef struct
{
    wchar_t *name;
    wchar_t *value;
} Var;

static Var g_vars[48];
static int g_count;

static void Fail(const wchar_t *message, DWORD error)
{
    wchar_t text[1024];
    wchar_t detail[512] = L"";
    if (error != 0)
    {
        FormatMessageW(FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS, NULL, error, 0, detail,
                       (DWORD)_countof(detail), NULL);
    }

    swprintf_s(text, _countof(text), L"%s\n\n%s", message, detail);
    MessageBoxW(NULL, text, TITLE, MB_OK | MB_ICONERROR);
}

static wchar_t *Dup(const wchar_t *text)
{
    size_t length = wcslen(text) + 1;
    wchar_t *copy = (wchar_t *)malloc(length * sizeof(wchar_t));
    if (copy != NULL)
    {
        wmemcpy(copy, text, length);
    }

    return copy;
}

static void Add(const wchar_t *name, const wchar_t *value)
{
    if (value == NULL || value[0] == L'\0' || g_count >= (int)_countof(g_vars))
    {
        return;
    }

    for (const wchar_t *p = value; *p; ++p)
    {
        if (*p == L'%')
        {
            return; // never pass on anything still holding an unexpanded variable
        }
    }

    wchar_t *n = Dup(name);
    wchar_t *v = Dup(value);
    if (n == NULL || v == NULL)
    {
        free(n);
        free(v);
        return;
    }

    g_vars[g_count].name = n;
    g_vars[g_count].value = v;
    g_count++;
}

// Replaces a leading %SystemDrive% or %SystemRoot% (as HKLM paths are stored) with the trusted values. Anything
// else that looks like a variable is left in place, and Add() then drops the value.
static void ExpandSystem(const wchar_t *raw, const wchar_t *systemDrive, const wchar_t *systemRoot, wchar_t *out, size_t outCount)
{
    static const wchar_t drive[] = L"%SystemDrive%";
    static const wchar_t root[] = L"%SystemRoot%";
    if (_wcsnicmp(raw, drive, _countof(drive) - 1) == 0)
    {
        swprintf_s(out, outCount, L"%s%s", systemDrive, raw + _countof(drive) - 1);
    }
    else if (_wcsnicmp(raw, root, _countof(root) - 1) == 0)
    {
        swprintf_s(out, outCount, L"%s%s", systemRoot, raw + _countof(root) - 1);
    }
    else
    {
        wcscpy_s(out, outCount, raw);
    }
}

// A string value from HKLM (writable by administrators only), read WITHOUT expanding variables - expansion
// would use this process's environment, which is exactly what must not be trusted.
static void AddFromHklm(const wchar_t *name, const wchar_t *key, const wchar_t *value, const wchar_t *systemDrive,
                        const wchar_t *systemRoot)
{
    wchar_t raw[MAX_PATH * 2];
    DWORD size = sizeof(raw);
    if (RegGetValueW(HKEY_LOCAL_MACHINE, key, value, RRF_RT_REG_SZ | RRF_RT_REG_EXPAND_SZ | RRF_NOEXPAND,
                     NULL, raw, &size) != ERROR_SUCCESS)
    {
        return;
    }

    wchar_t expanded[MAX_PATH * 2];
    ExpandSystem(raw, systemDrive, systemRoot, expanded, _countof(expanded));
    Add(name, expanded);
}

static int CompareVars(const void *a, const void *b)
{
    const Var *x = (const Var *)a;
    const Var *y = (const Var *)b;
    return CompareStringOrdinal(x->name, -1, y->name, -1, TRUE) - CSTR_EQUAL;
}

// name=value\0...\0\0, sorted by name as Windows expects.
static wchar_t *BuildEnvironmentBlock(void)
{
    qsort(g_vars, (size_t)g_count, sizeof(Var), CompareVars);
    size_t total = 1;
    for (int i = 0; i < g_count; i++)
    {
        total += wcslen(g_vars[i].name) + 1 + wcslen(g_vars[i].value) + 1;
    }

    wchar_t *block = (wchar_t *)calloc(total, sizeof(wchar_t));
    if (block == NULL)
    {
        return NULL;
    }

    wchar_t *p = block;
    for (int i = 0; i < g_count; i++)
    {
        size_t n = wcslen(g_vars[i].name);
        wmemcpy(p, g_vars[i].name, n);
        p += n;
        *p++ = L'=';
        n = wcslen(g_vars[i].value);
        wmemcpy(p, g_vars[i].value, n);
        p += n;
        *p++ = L'\0';
    }

    *p = L'\0';
    return block;
}

static void CollectEnvironment(void)
{
    // The Windows folder as the kernel knows it; %SystemRoot% and %windir% themselves can be overridden per user.
    wchar_t windows[MAX_PATH];
    UINT length = GetSystemWindowsDirectoryW(windows, MAX_PATH);
    if (length == 0 || length >= MAX_PATH)
    {
        return;
    }

    wchar_t systemDrive[3] = { windows[0], L':', L'\0' };
    wchar_t system32[MAX_PATH];
    wchar_t buffer[MAX_PATH * 4];
    swprintf_s(system32, _countof(system32), L"%s\\System32", windows);

    Add(L"SystemRoot", windows);
    Add(L"windir", windows);
    Add(L"SystemDrive", systemDrive);
    swprintf_s(buffer, _countof(buffer), L"%s\\cmd.exe", system32);
    Add(L"ComSpec", buffer);
    swprintf_s(buffer, _countof(buffer), L"%s;%s;%s\\Wbem;%s\\WindowsPowerShell\\v1.0\\", system32, windows, system32, system32);
    Add(L"PATH", buffer);
    Add(L"PATHEXT", L".COM;.EXE;.BAT;.CMD;.VBS;.VBE;.JS;.JSE;.WSF;.WSH;.MSC");
    Add(L"OS", L"Windows_NT");
    Add(L"PROCESSOR_ARCHITECTURE", L"AMD64");
    swprintf_s(buffer, _countof(buffer), L"%lu", GetActiveProcessorCount(ALL_PROCESSOR_GROUPS));
    Add(L"NUMBER_OF_PROCESSORS", buffer);

    DWORD size = (DWORD)_countof(buffer);
    if (GetComputerNameW(buffer, &size))
    {
        Add(L"COMPUTERNAME", buffer);
    }

    size = (DWORD)_countof(buffer);
    if (GetUserNameW(buffer, &size))
    {
        Add(L"USERNAME", buffer);
    }

    // The profile folder from the system's profile list (HKLM), not %USERPROFILE%; the usual folders under it.
    HANDLE token = NULL;
    if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token))
    {
        wchar_t profile[MAX_PATH * 2];
        size = (DWORD)_countof(profile);
        if (GetUserProfileDirectoryW(token, profile, &size) && wcschr(profile, L'%') == NULL)
        {
            Add(L"USERPROFILE", profile);
            if (profile[0] != L'\0' && profile[1] == L':')
            {
                wchar_t homeDrive[3] = { profile[0], L':', L'\0' };
                Add(L"HOMEDRIVE", homeDrive);
                Add(L"HOMEPATH", profile + 2);
            }

            swprintf_s(buffer, _countof(buffer), L"%s\\AppData\\Roaming", profile);
            Add(L"APPDATA", buffer);
            swprintf_s(buffer, _countof(buffer), L"%s\\AppData\\Local", profile);
            Add(L"LOCALAPPDATA", buffer);
            swprintf_s(buffer, _countof(buffer), L"%s\\AppData\\Local\\Temp", profile);
            Add(L"TEMP", buffer);
            Add(L"TMP", buffer);
        }

        CloseHandle(token);
    }

    static const wchar_t currentVersion[] = L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion";
    static const wchar_t profileList[] = L"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\ProfileList";
    AddFromHklm(L"ProgramFiles", currentVersion, L"ProgramFilesDir", systemDrive, windows);
    AddFromHklm(L"ProgramFiles(x86)", currentVersion, L"ProgramFilesDir (x86)", systemDrive, windows);
    AddFromHklm(L"ProgramW6432", currentVersion, L"ProgramW6432Dir", systemDrive, windows);
    AddFromHklm(L"CommonProgramFiles", currentVersion, L"CommonFilesDir", systemDrive, windows);
    AddFromHklm(L"CommonProgramFiles(x86)", currentVersion, L"CommonFilesDir (x86)", systemDrive, windows);
    AddFromHklm(L"CommonProgramW6432", currentVersion, L"CommonW6432Dir", systemDrive, windows);
    AddFromHklm(L"ProgramData", profileList, L"ProgramData", systemDrive, windows);
    AddFromHklm(L"ALLUSERSPROFILE", profileList, L"ProgramData", systemDrive, windows);
    AddFromHklm(L"PUBLIC", profileList, L"Public", systemDrive, windows);

    // No debugger, profiler or diagnostics channel can be attached to the elevated runtime.
    Add(L"DOTNET_EnableDiagnostics", L"0");
    Add(LAUNCHED_MARKER, L"1");
}

// Whether this process's environment is exactly the clean set (ignoring the per-drive "=C:" entries Windows keeps).
static BOOL EnvironmentIsClean(void)
{
    wchar_t *current = GetEnvironmentStringsW();
    if (current == NULL)
    {
        return FALSE;
    }

    int matched = 0;
    BOOL clean = TRUE;
    for (const wchar_t *entry = current; *entry != L'\0' && clean; entry += wcslen(entry) + 1)
    {
        // Per-drive "=C:" entries, and the compatibility layer Windows itself adds for a user's compatibility
        // settings on this exe (shims are Windows' own code; nothing here loads from it).
        if (entry[0] == L'=' || _wcsnicmp(entry, L"__COMPAT_LAYER=", 15) == 0)
        {
            continue;
        }

        const wchar_t *equals = wcschr(entry, L'=');
        size_t nameLength = equals == NULL ? wcslen(entry) : (size_t)(equals - entry);
        BOOL found = FALSE;
        for (int i = 0; i < g_count && !found; i++)
        {
            if (wcslen(g_vars[i].name) == nameLength &&
                CompareStringOrdinal(entry, (int)nameLength, g_vars[i].name, (int)nameLength, TRUE) == CSTR_EQUAL &&
                equals != NULL && wcscmp(equals + 1, g_vars[i].value) == 0)
            {
                found = TRUE;
            }
        }

        if (found)
        {
            matched++;
        }
        else
        {
            clean = FALSE;
        }
    }

    FreeEnvironmentStringsW(current);
    return clean && matched == g_count;
}

// Starts this exe again with exactly the clean environment; the copy that runs the app.
static int Relaunch(const wchar_t *self, const wchar_t *folder, wchar_t *environment)
{
    static wchar_t line[32800];
    if (swprintf_s(line, _countof(line), L"\"%s\"", self) < 0)
    {
        return 1;
    }

    STARTUPINFOW mine;
    GetStartupInfoW(&mine);
    STARTUPINFOW si;
    ZeroMemory(&si, sizeof(si));
    si.cb = sizeof(si);
    if ((mine.dwFlags & STARTF_USESHOWWINDOW) != 0)
    {
        si.dwFlags = STARTF_USESHOWWINDOW;
        si.wShowWindow = mine.wShowWindow;
    }

    PROCESS_INFORMATION pi;
    ZeroMemory(&pi, sizeof(pi));
    if (!CreateProcessW(self, line, NULL, NULL, FALSE, CREATE_UNICODE_ENVIRONMENT | CREATE_DEFAULT_ERROR_MODE,
                        environment, folder, &si, &pi))
    {
        Fail(L"Axis SD Card Reader could not be started.", GetLastError());
        return 1;
    }

    AllowSetForegroundWindow(pi.dwProcessId);
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    return 0;
}

static wchar_t g_errors[2048];

static void __cdecl CollectError(const wchar_t *message)
{
    size_t used = wcslen(g_errors);
    if (used + 2 < _countof(g_errors))
    {
        _snwprintf_s(g_errors + used, _countof(g_errors) - used, _TRUNCATE, L"%s\n", message);
    }
}

// --- The install folder -----------------------------------------------------------------------------------------
//
// Everything this process loads before the app's own code runs - hostfxr, the runtime, the framework, the app's
// assemblies and their .json files - comes from this exe's folder, so the folder must be one only administrators
// can change; otherwise any program running as the user could plant code that runs as administrator the moment the
// user approves the prompt for this (genuine) exe. This is checked here, before anything is loaded from the
// folder: the folder, the folders above it (whoever can rename one of them can swap the whole tree), and every file
// in it. The app checks the subfolders (libVLC, FFmpeg) itself before it uses them.

static PSID g_trusted[4]; // SYSTEM, Administrators, TrustedInstaller, CREATOR OWNER (inherit-only ACEs)

static BOOL IsTrusted(PSID sid)
{
    for (int i = 0; i < (int)_countof(g_trusted); i++)
    {
        if (g_trusted[i] != NULL && EqualSid(sid, g_trusted[i]))
        {
            return TRUE;
        }
    }

    return FALSE;
}

#define WRITE_RIGHTS (FILE_WRITE_DATA | FILE_APPEND_DATA | FILE_WRITE_EA | FILE_WRITE_ATTRIBUTES | DELETE | \
                      FILE_DELETE_CHILD | WRITE_DAC | WRITE_OWNER)
#define ANCESTOR_RIGHTS (DELETE | FILE_DELETE_CHILD | WRITE_DAC | WRITE_OWNER)

// Whether anyone but SYSTEM, Administrators or TrustedInstaller owns the object open in 'handle', or is granted any
// of 'rights' on it. Unknown counts as yes.
static BOOL OthersCanChange(HANDLE handle, DWORD rights)
{
    PSID owner = NULL;
    PACL dacl = NULL;
    PSECURITY_DESCRIPTOR descriptor = NULL;
    if (GetSecurityInfo(handle, SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION, &owner, NULL, &dacl,
                        NULL, &descriptor) != ERROR_SUCCESS)
    {
        return TRUE;
    }

    BOOL changeable = owner == NULL || !IsTrusted(owner) || dacl == NULL; // a NULL DACL grants everyone everything
    for (DWORD i = 0; !changeable && i < dacl->AceCount; i++)
    {
        ACE_HEADER *ace = NULL;
        if (!GetAce(dacl, i, (LPVOID *)&ace))
        {
            changeable = TRUE;
            break;
        }

        if ((ace->AceFlags & INHERIT_ONLY_ACE) != 0 ||
            (ace->AceType != ACCESS_ALLOWED_ACE_TYPE && ace->AceType != ACCESS_ALLOWED_CALLBACK_ACE_TYPE))
        {
            continue;
        }

        ACCESS_ALLOWED_ACE *allowed = (ACCESS_ALLOWED_ACE *)ace;
        DWORD mask = allowed->Mask;
        if ((mask & GENERIC_ALL) != 0)
        {
            mask |= FILE_ALL_ACCESS;
        }

        if ((mask & GENERIC_WRITE) != 0)
        {
            mask |= FILE_GENERIC_WRITE;
        }

        if ((mask & rights) != 0 && !IsTrusted((PSID)&allowed->SidStart))
        {
            changeable = TRUE;
        }
    }

    LocalFree(descriptor);
    return changeable;
}

// Opens a file or folder itself (a link is opened as the link, never followed) just to read its security.
static HANDLE OpenForCheck(const wchar_t *path)
{
    return CreateFileW(path, READ_CONTROL | FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL,
                       OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, NULL);
}

// One file or folder: not a link, and nobody else can change it in the ways that matter. On failure, says why.
static BOOL CheckItem(const wchar_t *path, DWORD rights, wchar_t *why, size_t whyCount)
{
    HANDLE handle = OpenForCheck(path);
    if (handle == INVALID_HANDLE_VALUE)
    {
        swprintf_s(why, whyCount, L"%s could not be checked.", path);
        return FALSE;
    }

    BY_HANDLE_FILE_INFORMATION info;
    BOOL ok = GetFileInformationByHandle(handle, &info);
    if (!ok || (info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
    {
        swprintf_s(why, whyCount, L"%s is a link (it may lead to a folder other programs can change).", path);
        CloseHandle(handle);
        return FALSE;
    }

    if (OthersCanChange(handle, rights))
    {
        swprintf_s(why, whyCount, L"%s can be changed without administrator rights.", path);
        CloseHandle(handle);
        return FALSE;
    }

    CloseHandle(handle);
    return TRUE;
}

static BOOL InstallFolderIsSafe(const wchar_t *folder, wchar_t *why, size_t whyCount)
{
    static const wchar_t *trusted[] = {
        L"S-1-5-18", L"S-1-5-32-544", L"S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", L"S-1-3-0",
    };
    for (int i = 0; i < (int)_countof(trusted); i++)
    {
        if (g_trusted[i] == NULL && !ConvertStringSidToSidW(trusted[i], &g_trusted[i]))
        {
            swprintf_s(why, whyCount, L"The app's folder could not be checked.");
            return FALSE;
        }
    }

    if (folder[0] == L'\\')
    {
        swprintf_s(why, whyCount, L"%s is a network folder.", folder);
        return FALSE;
    }

    HANDLE handle = OpenForCheck(folder);
    if (handle == INVALID_HANDLE_VALUE)
    {
        swprintf_s(why, whyCount, L"%s could not be checked.", folder);
        return FALSE;
    }

    DWORD flags = 0;
    BOOL known = GetVolumeInformationByHandleW(handle, NULL, 0, NULL, NULL, &flags, NULL, 0);
    CloseHandle(handle);
    if (!known || (flags & FILE_PERSISTENT_ACLS) == 0)
    {
        swprintf_s(why, whyCount, L"%s is on a drive without file permissions (FAT or exFAT, say), which any program can change.", folder);
        return FALSE;
    }

    // The folder and the one above it: nobody else may add, replace or delete anything in them.
    static wchar_t path[32768];
    wcscpy_s(path, _countof(path), folder);
    if (!CheckItem(path, WRITE_RIGHTS, why, whyCount))
    {
        return FALSE;
    }

    for (int level = 0;; level++)
    {
        wchar_t *slash = wcsrchr(path, L'\\');
        if (slash == NULL || slash == path || (slash == path + 2 && path[1] == L':'))
        {
            if (slash != NULL && slash == path + 2 && slash[1] != L'\0')
            {
                slash[1] = L'\0'; // the drive's root, "C:\"
                if (!CheckItem(path, level == 0 ? WRITE_RIGHTS : ANCESTOR_RIGHTS, why, whyCount))
                {
                    return FALSE;
                }
            }

            break;
        }

        *slash = L'\0';
        if (!CheckItem(path, level == 0 ? WRITE_RIGHTS : ANCESTOR_RIGHTS, why, whyCount))
        {
            return FALSE;
        }
    }

    // Every file directly in the folder, and no links among its entries.
    static wchar_t pattern[32768];
    swprintf_s(pattern, _countof(pattern), L"%s\\*", folder);
    WIN32_FIND_DATAW entry;
    HANDLE find = FindFirstFileExW(pattern, FindExInfoBasic, &entry, FindExSearchNameMatch, NULL, FIND_FIRST_EX_LARGE_FETCH);
    if (find == INVALID_HANDLE_VALUE)
    {
        swprintf_s(why, whyCount, L"The files in %s could not be checked.", folder);
        return FALSE;
    }

    BOOL safe = TRUE;
    do
    {
        if (wcscmp(entry.cFileName, L".") == 0 || wcscmp(entry.cFileName, L"..") == 0)
        {
            continue;
        }

        swprintf_s(path, _countof(path), L"%s\\%s", folder, entry.cFileName);
        if ((entry.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
        {
            swprintf_s(why, whyCount, L"%s is a link (it may lead to a folder other programs can change).", path);
            safe = FALSE;
        }
        else if ((entry.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0)
        {
            safe = CheckItem(path, WRITE_RIGHTS, why, whyCount);
        }
    } while (safe && FindNextFileW(find, &entry));

    FindClose(find);
    return safe;
}

static BOOL IsElevated(void)
{
    HANDLE token = NULL;
    TOKEN_ELEVATION elevation = { 0 };
    DWORD size = 0;
    BOOL elevated = OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token) &&
                    GetTokenInformation(token, TokenElevation, &elevation, sizeof(elevation), &size) &&
                    elevation.TokenIsElevated != 0;
    if (token != NULL)
    {
        CloseHandle(token);
    }

    return elevated;
}

// This exe's real path: what Windows reports for the file itself, with no link, junction or short name in it. The
// path the exe was started by must already be that; one reached through a link is refused (a link can be changed to
// lead elsewhere between now and the relaunch below).
static BOOL ResolveOwnPath(wchar_t *self, size_t selfCount)
{
    static wchar_t module[32768];
    static wchar_t longModule[32768];
    DWORD length = GetModuleFileNameW(NULL, module, _countof(module));
    if (length == 0 || length >= _countof(module))
    {
        return FALSE;
    }

    HANDLE file = CreateFileW(module, FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL,
                              OPEN_EXISTING, 0, NULL);
    if (file == INVALID_HANDLE_VALUE)
    {
        return FALSE;
    }

    DWORD finalLength = GetFinalPathNameByHandleW(file, self, (DWORD)selfCount, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
    CloseHandle(file);
    if (finalLength == 0 || finalLength >= selfCount)
    {
        return FALSE;
    }

    // "\\?\C:\..." to "C:\..."; a network path ("\\?\UNC\...") is left alone and refused by the folder check.
    if (wcsncmp(self, L"\\\\?\\", 4) == 0 && self[5] == L':')
    {
        wmemmove(self, self + 4, wcslen(self + 4) + 1);
    }

    const wchar_t *started = module;
    if (wcsncmp(started, L"\\\\?\\", 4) == 0 && started[5] == L':')
    {
        started += 4;
    }

    length = GetLongPathNameW(started, longModule, _countof(longModule));
    if (length > 0 && length < _countof(longModule))
    {
        started = longModule;
    }

    return CompareStringOrdinal(self, -1, started, -1, TRUE) == CSTR_EQUAL;
}

// The same mitigations the app applies to itself, but before the runtime loads, so they cover its startup too:
// never follow a junction or symbolic link a non-administrator created, and never load an image from a remote
// share or with a low-integrity label.
static void ApplyMitigations(void)
{
    // ProcessRedirectionTrustPolicy (16) with EnforceRedirectionTrust; spelled out because older SDKs lack the names.
    DWORD redirection = 0x1;
    SetProcessMitigationPolicy((PROCESS_MITIGATION_POLICY)16, &redirection, sizeof(redirection));

    PROCESS_MITIGATION_IMAGE_LOAD_POLICY images = { 0 };
    images.NoRemoteImages = 1;
    images.NoLowMandatoryLabelImages = 1;
    images.PreferSystem32Images = 1;
    SetProcessMitigationPolicy(ProcessImageLoadPolicy, &images, sizeof(images));
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR commandLine, int show)
{
    (void)instance;
    (void)previous;
    (void)commandLine;
    (void)show;

    HeapSetInformation(NULL, HeapEnableTerminationOnCorruption, NULL, 0);
    SetDllDirectoryW(L""); // the current directory is never searched for DLLs
    ApplyMitigations();    // before any path is looked up: no link a non-administrator made is followed from here on

    // Everything lives beside this exe, at its real location.
    static wchar_t self[32768];
    if (!ResolveOwnPath(self, _countof(self)))
    {
        Fail(L"Axis SD Card Reader was started through a link or shortcut path that leads somewhere else. "
             L"Start AxisSdReader.exe from its own folder (C:\\Program Files\\Axis SD Card Reader).", 0);
        return 1;
    }

    static wchar_t folder[32768];
    wcscpy_s(folder, _countof(folder), self);
    wchar_t *slash = wcsrchr(folder, L'\\');
    if (slash == NULL || slash == folder + 2)
    {
        Fail(L"Axis SD Card Reader can't run from the top of a drive. Extract it into C:\\Program Files.", 0);
        return 1;
    }

    *slash = L'\0';

    // Elevated, nothing may be loaded from a folder that programs without administrator rights can change.
    if (IsElevated())
    {
        static wchar_t why[33000];
        if (!InstallFolderIsSafe(folder, why, _countof(why)))
        {
            static wchar_t text[34000];
            swprintf_s(text, _countof(text),
                       L"Axis SD Card Reader won't start from this folder, because programs running without administrator "
                       L"rights could change it:\n\n%s\n\nThat would let any program on this PC run code as administrator "
                       L"through this app. Delete this copy, download the release again (check its SHA-256), and extract "
                       L"it into C:\\Program Files.", why);
            MessageBoxW(NULL, text, TITLE, MB_OK | MB_ICONERROR);
            return 1;
        }
    }
    static wchar_t app[32768];
    static wchar_t hostfxr[32768];
    if (swprintf_s(app, _countof(app), L"%s\\%s", folder, APP_DLL) < 0 ||
        swprintf_s(hostfxr, _countof(hostfxr), L"%s\\%s", folder, HOSTFXR_DLL) < 0)
    {
        return 1;
    }

    if (GetFileAttributesW(app) == INVALID_FILE_ATTRIBUTES || GetFileAttributesW(hostfxr) == INVALID_FILE_ATTRIBUTES)
    {
        Fail(L"Part of the app is missing next to AxisSdReader.exe. Reinstall it (unzip the whole folder).", 0);
        return 1;
    }

    CollectEnvironment();
    if (g_count == 0)
    {
        Fail(L"Axis SD Card Reader could not prepare a safe environment to start in.", GetLastError());
        return 1;
    }

    wchar_t *environment = BuildEnvironmentBlock();
    if (environment == NULL)
    {
        return 1;
    }

    if (!EnvironmentIsClean())
    {
        // A copy this launcher started has exactly the clean environment; one that doesn't but carries the marker
        // was started some other way with a tampered environment, so it stops rather than run with it.
        if (GetEnvironmentVariableW(LAUNCHED_MARKER, NULL, 0) != 0)
        {
            Fail(L"Axis SD Card Reader could not start in a clean environment.", 0);
            return 1;
        }

        return Relaunch(self, folder, environment);
    }

    if (!SetCurrentDirectoryW(folder))
    {
        Fail(L"Axis SD Card Reader could not open its own folder.", GetLastError());
        return 1;
    }

    HMODULE fxr = LoadLibraryExW(hostfxr, NULL, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (fxr == NULL)
    {
        Fail(L"The app's .NET runtime could not be loaded. Reinstall the app (unzip the whole folder).", GetLastError());
        return 1;
    }

    hostfxr_main_startupinfo_fn run = (hostfxr_main_startupinfo_fn)(void *)GetProcAddress(fxr, "hostfxr_main_startupinfo");
    hostfxr_set_error_writer_fn setErrorWriter = (hostfxr_set_error_writer_fn)(void *)GetProcAddress(fxr, "hostfxr_set_error_writer");
    if (run == NULL)
    {
        Fail(L"The app's .NET runtime is incomplete. Reinstall the app (unzip the whole folder).", GetLastError());
        return 1;
    }

    if (setErrorWriter != NULL)
    {
        setErrorWriter(CollectError);
    }

    // Self-contained: the runtime's root is this folder. argv carries only this exe's path.
    const wchar_t *argv[] = { self };
    int code = run(1, argv, self, folder, app);
    if (code != 0 && g_errors[0] != L'\0')
    {
        wchar_t text[2304];
        swprintf_s(text, _countof(text), L"Axis SD Card Reader could not start.\n\n%s", g_errors);
        MessageBoxW(NULL, text, TITLE, MB_OK | MB_ICONERROR);
    }

    return code;
}
