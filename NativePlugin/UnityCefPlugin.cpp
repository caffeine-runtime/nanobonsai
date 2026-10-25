// Offscreen CEF bridge for HtmlUiView on Windows (CEF's own UI thread) and macOS (pumped from Unity's main thread,
// UnityCefMac.mm): browsers paint into CPU buffers the view copies, and page console lines prefixed "unity:" reach C#.
// Under Unity's D3D12 a Windows browser hands frames over as shared GPU textures instead: copied on CEF's thread into
// frames of its own, then into the view's texture on Unity's render thread (headers in unity/, Unity Companion License).
#ifdef _WIN32
#include <windows.h>
#include <atomic>
// MinGW's D3D12 methods returning structs need the explicit-return form to match the COM ABI.
#define WIDL_EXPLICIT_AGGREGATE_RETURNS
#include <d3d12.h>
#include "unity/IUnityGraphics.h"
#include "unity/IUnityGraphicsD3D12.h"
#define UNITY_CEF_EXPORT __declspec(dllexport)
#else
#include <crt_externs.h>
#include <signal.h>
#include <unistd.h>
#define UNITY_CEF_EXPORT __attribute__((visibility("default")))
#endif
#include <cerrno>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>
#include <mutex>
#include <map>
#include <iostream>
#include <filesystem>
#include "include/cef_app.h"
#include "include/cef_client.h"
#include "include/cef_render_handler.h"
#include "include/cef_display_handler.h"
#include "include/cef_life_span_handler.h"

// Callback signatures
typedef void (*MessageCallback)(int browserId, const char* json);

// Forward declarations
class CustomClient;

struct BrowserInstance {
    int id;
    CefRefPtr<CefBrowser> browser;
    CefRefPtr<CustomClient> client;
    bool isCreated = false;
};

// Globals
std::map<int, BrowserInstance> g_browsers;
std::recursive_mutex g_browsers_mutex;
int g_next_browser_id = 1;
// Paints per second for browsers created from now on; HtmlUiView sets the display's refresh before its first.
int g_frame_rate = 90;
MessageCallback g_message_callback = nullptr;
bool g_cef_initialized = false;
bool g_cef_initialization_attempted = false;
std::filesystem::path g_cef_root;
// A browser closed from the UI thread may run OnBeforeClose inside CloseBrowser, under the caller's lock.
using BrowsersLock = std::lock_guard<std::recursive_mutex>;

static bool ProcessRunning(unsigned long pid) {
#ifdef _WIN32
    HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, static_cast<DWORD>(pid));
    if (!process) return GetLastError() != ERROR_INVALID_PARAMETER;  // denied means it exists
    DWORD code = 0;
    bool running = GetExitCodeProcess(process, &code) && code == STILL_ACTIVE;
    CloseHandle(process);
    return running;
#else
    return kill(static_cast<pid_t>(pid), 0) == 0 || errno != ESRCH;
#endif
}

// Roots and logs (<pid>, <pid>.log) of processes no longer running: the Editor never shuts CEF down, a crash cannot,
// and a process cannot delete the log it holds open. Non-throwing calls only; the Mac build has no exceptions.
static void RemoveStaleRoots() {
    std::error_code error;
    std::filesystem::directory_iterator end;
    for (std::filesystem::directory_iterator it(g_cef_root.parent_path(), error); !error && it != end; it.increment(error)) {
        std::string name = it->path().filename().string();
        if (name.size() > 4 && name.compare(name.size() - 4, 4, ".log") == 0) name.resize(name.size() - 4);
        if (name.empty() || name.size() > 10 || name.find_first_not_of("0123456789") != std::string::npos) continue;
        unsigned long pid = std::strtoul(name.c_str(), nullptr, 10);
        std::error_code ignored;
        if (name != g_cef_root.filename().string() && !ProcessRunning(pid)) std::filesystem::remove_all(it->path(), ignored);
    }
}

#ifdef _WIN32
// Unity's D3D12 interface while the game renders with D3D12; null otherwise, and then browsers paint on the CPU.
IUnityInterfaces* g_unity = nullptr;
IUnityGraphicsD3D12v7* g_d3d12 = nullptr;
// Held by CEF's thread while it copies on Unity's device, so the device's shutdown waits for the copy.
std::mutex g_device_mutex;
// Set by the first browser whose hand-off fails; browsers created afterwards paint on the CPU.
std::atomic<bool> g_shared_texture_failed{false};
// The render-thread copy's plugin event id; HtmlUiView issues the same.
const int kSharedFrameEvent = 0x4345F001;

static bool Reached(ID3D12Fence* fence, UINT64 value) {
    return fence->GetCompletedValue() >= value;
}

static bool WaitForFence(ID3D12Fence* fence, UINT64 value, DWORD milliseconds) {
    if (Reached(fence, value)) return true;
    HANDLE event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    bool reached = event && SUCCEEDED(fence->SetEventOnCompletion(value, event)) && WaitForSingleObject(event, milliseconds) == WAIT_OBJECT_0;
    if (event) CloseHandle(event);
    return reached;
}

static void UNITY_INTERFACE_API OnGraphicsDeviceEvent(UnityGfxDeviceEventType event) {
    if (event == kUnityGfxDeviceEventInitialize && g_unity->Get<IUnityGraphics>()->GetRenderer() == kUnityGfxRendererD3D12) {
        g_d3d12 = g_unity->Get<IUnityGraphicsD3D12v7>();
        // As Unity's D3D12 sample configures its ExecuteCommandList events.
        UnityD3D12PluginEventConfig config = {};
        config.graphicsQueueAccess = kUnityD3D12GraphicsQueueAccess_DontCare;
        config.flags = kUnityD3D12EventConfigFlag_SyncWorkerThreads | kUnityD3D12EventConfigFlag_ModifiesCommandBuffersState
            | kUnityD3D12EventConfigFlag_EnsurePreviousFrameSubmission;
        config.ensureActiveRenderTextureIsBound = false;
        if (g_d3d12) g_d3d12->ConfigureEvent(kSharedFrameEvent, &config);
    } else if (event == kUnityGfxDeviceEventShutdown) {
        std::lock_guard<std::mutex> lock(g_device_mutex);
        g_d3d12 = nullptr;
    }
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* interfaces) {
    g_unity = interfaces;
    interfaces->Get<IUnityGraphics>()->RegisterDeviceEventCallback(OnGraphicsDeviceEvent);
    OnGraphicsDeviceEvent(kUnityGfxDeviceEventInitialize);
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginUnload() {
    g_unity->Get<IUnityGraphics>()->UnregisterDeviceEventCallback(OnGraphicsDeviceEvent);
}

// Render-thread command lists for the copies into views' textures, reused once Unity's frame fence passes them;
// a list still in flight is never waited for, since Unity's frame cannot end while its render thread waits.
struct UploadList {
    ID3D12CommandAllocator* allocator = nullptr;
    ID3D12GraphicsCommandList* list = nullptr;
    UINT64 done = 0;
};
UploadList g_upload_lists[8];
int g_next_upload_list = 0;

static ID3D12GraphicsCommandList* NextUploadList(UploadList*& upload) {
    upload = &g_upload_lists[g_next_upload_list++ % 8];
    if (!upload->list) {
        ID3D12Device* device = g_d3d12->GetDevice();
        if (FAILED(device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&upload->allocator)))) return nullptr;
        if (FAILED(device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, upload->allocator, nullptr, IID_PPV_ARGS(&upload->list)))) {
            upload->allocator->Release();
            upload->allocator = nullptr;
            return nullptr;
        }
        upload->list->Close();
    }
    if (!Reached(g_d3d12->GetFrameFence(), upload->done)) return nullptr;
    if (FAILED(upload->allocator->Reset()) || FAILED(upload->list->Reset(upload->allocator, nullptr))) return nullptr;
    return upload->list;
}

static void Transition(ID3D12GraphicsCommandList* list, ID3D12Resource* resource, D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after) {
    D3D12_RESOURCE_BARRIER barrier = {};
    barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barrier.Transition.pResource = resource;
    barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    barrier.Transition.StateBefore = before;
    barrier.Transition.StateAfter = after;
    list->ResourceBarrier(1, &barrier);
}

static bool IsBgra(DXGI_FORMAT format) {
    return format == DXGI_FORMAT_B8G8R8A8_UNORM || format == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB || format == DXGI_FORMAT_B8G8R8A8_TYPELESS;
}
#endif

#ifdef __APPLE__
extern "C" bool UnityCefLoadFramework();
extern "C" void UnityCefAdoptApplication();
bool TranslateMacKey(CefKeyEvent& key_event);
#endif

class UnityCefApp : public CefApp {
public:
    void OnBeforeCommandLineProcessing(const CefString& process_type,
                                      CefRefPtr<CefCommandLine> command_line) override {
        // Chromium cannot relaunch its Unity host: it leaves a failed window and rewrites Unity's arguments.
        if (process_type.empty()) command_line->AppendSwitch("do-not-de-elevate");
#ifdef _WIN32
        // Chromium renders on Unity's adapter, so a laptop's two GPUs can still share textures.
        if (process_type.empty() && g_d3d12) {
            LUID luid = g_d3d12->GetDevice()->GetAdapterLuid();
            command_line->AppendSwitchWithValue("use-adapter-luid", std::to_string(luid.HighPart) + "," + std::to_string(luid.LowPart));
        }
#endif
#ifdef __APPLE__
        // The pages keep no cookies or passwords, so Chromium never asks the player for the keychain.
        if (process_type.empty()) command_line->AppendSwitch("use-mock-keychain");
#endif
    }

    IMPLEMENT_REFCOUNTING(UnityCefApp);
};

// Custom Render Handler for Offscreen Rendering
class CustomRenderHandler : public CefRenderHandler {
private:
    int width_ = 800;
    int height_ = 600;
    std::vector<unsigned char> pixel_buffer_;
    std::mutex buffer_mutex_;
    bool is_dirty_ = false;
#ifdef _WIN32
    // Three frames rotate; CEF's thread copies only into one Unity has finished reading and never waits on Unity, whose
    // frame may stall (a scene load, quitting). With none free the paint is dropped and the page asked to repaint.
    ID3D12Resource* frames_[3] = {};
    UINT64 frame_read_[3] = {};  // Unity's frame-fence value once its copy out of the frame has finished
    int latest_ = -1;
    bool shared_dirty_ = false, shared_failed_ = false, shared_missed_ = false;
    ID3D12Resource* target_ = nullptr;  // the view's texture, Unity's
    ID3D12CommandQueue* queue_ = nullptr;
    ID3D12CommandAllocator* allocator_ = nullptr;
    ID3D12GraphicsCommandList* list_ = nullptr;
    ID3D12Fence* fence_ = nullptr;
    UINT64 fence_value_ = 0;

    void FailSharedTexture() {
        std::lock_guard<std::mutex> lock(buffer_mutex_);
        shared_failed_ = true;
        g_shared_texture_failed = true;
    }

    // Chromium's pooled texture returns to its pool when OnAcceleratedPaint ends, so the copy is waited for.
    bool CopyFrame(ID3D12Resource* source, int index) {
        ID3D12Device* device = g_d3d12->GetDevice();
        if (!queue_) {
            D3D12_COMMAND_QUEUE_DESC queue_desc = {};
            queue_desc.Type = D3D12_COMMAND_LIST_TYPE_COPY;
            if (FAILED(device->CreateCommandQueue(&queue_desc, IID_PPV_ARGS(&queue_)))
                || FAILED(device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_COPY, IID_PPV_ARGS(&allocator_)))
                || FAILED(device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_COPY, allocator_, nullptr, IID_PPV_ARGS(&list_)))
                || FAILED(device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence_)))) return false;
            list_->Close();
        }
        D3D12_RESOURCE_DESC desc = source->GetDesc();
        ID3D12Resource*& frame = frames_[index];
        if (frame) {
            D3D12_RESOURCE_DESC have = frame->GetDesc();
            if (have.Width != desc.Width || have.Height != desc.Height || have.Format != desc.Format) {
                frame->Release();
                frame = nullptr;
            }
        }
        if (!frame) {
            D3D12_HEAP_PROPERTIES heap = {};
            heap.Type = D3D12_HEAP_TYPE_DEFAULT;
            D3D12_RESOURCE_DESC frame_desc = {};
            frame_desc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
            frame_desc.Width = desc.Width;
            frame_desc.Height = desc.Height;
            frame_desc.DepthOrArraySize = 1;
            frame_desc.MipLevels = 1;
            frame_desc.Format = desc.Format;
            frame_desc.SampleDesc.Count = 1;
            if (FAILED(device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &frame_desc, D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&frame)))) return false;
        }
        // Both textures sit in COMMON and promote to the copy states implicitly; the copy queue decays them back.
        if (FAILED(allocator_->Reset()) || FAILED(list_->Reset(allocator_, nullptr))) return false;
        list_->CopyResource(frame, source);
        if (FAILED(list_->Close())) return false;
        ID3D12CommandList* lists[] = { list_ };
        queue_->ExecuteCommandLists(1, lists);
        queue_->Signal(fence_, ++fence_value_);
        return WaitForFence(fence_, fence_value_, INFINITE);
    }
#endif

public:
    CustomRenderHandler(int width, int height) : width_(width), height_(height) {
        pixel_buffer_.resize(width * height * 4, 0);
    }

#ifdef _WIN32
    ~CustomRenderHandler() {
        std::lock_guard<std::mutex> device_lock(g_device_mutex);
        // A frame Unity may still be reading after half a second (its frame stalled) is left to the process's end.
        for (int i = 0; i < 3; i++)
            if (frames_[i] && (!g_d3d12 || WaitForFence(g_d3d12->GetFrameFence(), frame_read_[i], 500))) frames_[i]->Release();
        if (list_) list_->Release();
        if (allocator_) allocator_->Release();
        if (queue_) queue_->Release();
        if (fence_) fence_->Release();
    }

    void OnAcceleratedPaint(CefRefPtr<CefBrowser> browser, PaintElementType type, const RectList& dirtyRects, const CefAcceleratedPaintInfo& info) override {
        std::lock_guard<std::mutex> device_lock(g_device_mutex);
        if (type != PET_VIEW || !g_d3d12) return;
        int next = -1;
        {
            std::lock_guard<std::mutex> lock(buffer_mutex_);
            if (shared_failed_) return;
            for (int step = 1; step < 3 && next < 0; step++) {
                int index = (latest_ + 3 + step) % 3;
                if (Reached(g_d3d12->GetFrameFence(), frame_read_[index])) next = index;
            }
            if (next < 0) {
                shared_missed_ = true;
                return;
            }
        }
        ID3D12Resource* source = nullptr;
        if (info.format != CEF_COLOR_TYPE_BGRA_8888
            || FAILED(g_d3d12->GetDevice()->OpenSharedHandle(info.shared_texture_handle, IID_PPV_ARGS(&source)))) {
            std::cerr << "CEF: the shared texture cannot be opened on Unity's device." << std::endl;
            FailSharedTexture();
            return;
        }
        bool copied = CopyFrame(source, next);
        source->Release();
        if (!copied) {
            std::cerr << "CEF: the shared texture cannot be copied." << std::endl;
            FailSharedTexture();
            return;
        }
        std::lock_guard<std::mutex> lock(buffer_mutex_);
        latest_ = next;
        shared_dirty_ = true;
    }

    void SetSharedTarget(ID3D12Resource* target) {
        std::lock_guard<std::mutex> lock(buffer_mutex_);
        target_ = target;
    }

    // 1 when a frame arrived since the last call, -1 once the hand-off has failed; missed reports a dropped paint.
    int TakeSharedFrame(bool& missed) {
        std::lock_guard<std::mutex> lock(buffer_mutex_);
        if (shared_failed_) return -1;
        missed = shared_missed_;
        shared_missed_ = false;
        bool dirty = shared_dirty_;
        shared_dirty_ = false;
        return dirty ? 1 : 0;
    }

    // Unity's render thread: the newest frame into the view's texture, through Unity's queue so it tracks the state.
    void Upload() {
        std::lock_guard<std::mutex> lock(buffer_mutex_);
        if (latest_ < 0 || !target_ || !g_d3d12 || shared_failed_) return;
        ID3D12Resource* frame = frames_[latest_];
        D3D12_RESOURCE_DESC have = frame->GetDesc(), want = target_->GetDesc();
        if (have.Width != want.Width || have.Height != want.Height) return;
        if (!IsBgra(want.Format) || !IsBgra(have.Format)) {
            shared_failed_ = true;
            g_shared_texture_failed = true;
            return;
        }
        UploadList* upload;
        ID3D12GraphicsCommandList* list = NextUploadList(upload);
        if (!list) {
            shared_dirty_ = true;  // tried again next frame
            return;
        }
        Transition(list, target_, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_COPY_DEST);
        list->CopyResource(target_, frame);
        Transition(list, target_, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_COMMON);
        if (FAILED(list->Close())) return;
        UnityGraphicsD3D12ResourceState state = { target_, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_COMMON };
        upload->done = frame_read_[latest_] = g_d3d12->ExecuteCommandList(list, 1, &state);
    }
#endif

    void Resize(int width, int height) {
        std::lock_guard<std::mutex> lock(buffer_mutex_);
        width_ = width;
        height_ = height;
        pixel_buffer_.resize(width * height * 4, 0);
    }

    void GetViewRect(CefRefPtr<CefBrowser> browser, CefRect& rect) override {
        rect.Set(0, 0, width_, height_);
    }

    void OnPaint(CefRefPtr<CefBrowser> browser, PaintElementType type, const RectList& dirtyRects, const void* buffer, int width, int height) override {
        if (type != PET_VIEW) return;
        std::lock_guard<std::mutex> lock(buffer_mutex_);
        if (width == width_ && height == height_) {
            memcpy(pixel_buffer_.data(), buffer, width * height * 4);
            is_dirty_ = true;
        }
    }

    bool GetTextureBuffer(unsigned char* destBuffer, int destSize, int& outWidth, int& outHeight) {
        std::lock_guard<std::mutex> lock(buffer_mutex_);
        if (pixel_buffer_.empty()) return false;
        int neededSize = width_ * height_ * 4;
        if (destSize < neededSize) return false;
        memcpy(destBuffer, pixel_buffer_.data(), neededSize);
        outWidth = width_;
        outHeight = height_;
        is_dirty_ = false;
        return true;
    }

    bool IsDirty() {
        return is_dirty_;
    }

    IMPLEMENT_REFCOUNTING(CustomRenderHandler);
};

// Custom Display Handler to intercept console logs for JS-to-Unity messaging
class CustomDisplayHandler : public CefDisplayHandler {
private:
    int browser_id_;

public:
    CustomDisplayHandler(int browser_id) : browser_id_(browser_id) {}

    bool OnConsoleMessage(CefRefPtr<CefBrowser> browser,
                          cef_log_severity_t severity,
                          const CefString& message,
                          const CefString& source,
                          int line) override {
        std::string msg_str = message.ToString();
        // Check if the message starts with "unity:"
        if (msg_str.rfind("unity:", 0) == 0) {
            std::string json = msg_str.substr(6);
            if (g_message_callback) {
                g_message_callback(browser_id_, json.c_str());
            }
            return true; // Suppress console output for the custom messaging
        }
        return false;
    }

    IMPLEMENT_REFCOUNTING(CustomDisplayHandler);
};

// Custom Life Span Handler to track browser lifecycle
class CustomLifeSpanHandler : public CefLifeSpanHandler {
private:
    int browser_id_;

public:
    CustomLifeSpanHandler(int browser_id) : browser_id_(browser_id) {}

    void OnAfterCreated(CefRefPtr<CefBrowser> browser) override {
        BrowsersLock lock(g_browsers_mutex);
        auto it = g_browsers.find(browser_id_);
        if (it != g_browsers.end()) {
            it->second.browser = browser;
            it->second.isCreated = true;
        }
    }

    void OnBeforeClose(CefRefPtr<CefBrowser> browser) override {
        BrowsersLock lock(g_browsers_mutex);
        auto it = g_browsers.find(browser_id_);
        if (it != g_browsers.end()) {
            it->second.browser = nullptr;
            it->second.isCreated = false;
        }
    }

    IMPLEMENT_REFCOUNTING(CustomLifeSpanHandler);
};

// Custom Client to link handlers together
class CustomClient : public CefClient {
private:
    int browser_id_;
    CefRefPtr<CustomRenderHandler> render_handler_;
    CefRefPtr<CustomDisplayHandler> display_handler_;
    CefRefPtr<CustomLifeSpanHandler> lifespan_handler_;

public:
    CustomClient(int browser_id, int width, int height) : browser_id_(browser_id) {
        render_handler_ = new CustomRenderHandler(width, height);
        display_handler_ = new CustomDisplayHandler(browser_id);
        lifespan_handler_ = new CustomLifeSpanHandler(browser_id);
    }

    CefRefPtr<CefRenderHandler> GetRenderHandler() override {
        return render_handler_;
    }

    CefRefPtr<CefDisplayHandler> GetDisplayHandler() override {
        return display_handler_;
    }

    CefRefPtr<CefLifeSpanHandler> GetLifeSpanHandler() override {
        return lifespan_handler_;
    }

    CustomRenderHandler* GetCustomRenderHandler() {
        return render_handler_.get();
    }

    IMPLEMENT_REFCOUNTING(CustomClient);
};

// DLL Exports
extern "C" {

    UNITY_CEF_EXPORT bool InitCEF(const char* subprocessPath, const char* localesPath, const char* resourcesPath) {
        if (g_cef_initialized) {
            return true;
        }
        if (g_cef_initialization_attempted) return false;

        CefSettings settings;
        settings.no_sandbox = true;
        settings.windowless_rendering_enabled = true;
        settings.background_color = 0x00000000; // Fully transparent

#ifdef __APPLE__
        // The framework loads at runtime from Contents/Frameworks; Unity's NSApp gains the protocol CEF requires,
        // and the page runs on the calling (main) thread through PumpCEF.
        if (!UnityCefLoadFramework()) {
            g_cef_initialization_attempted = true;
            std::cerr << "CEF: cannot load the Chromium Embedded Framework." << std::endl;
            return false;
        }
        UnityCefAdoptApplication();
        CefMainArgs main_args(*_NSGetArgc(), *_NSGetArgv());
        // Never inside the app: its bundle is sealed by the signature and read-only once macOS translocates it.
        std::error_code temporaryError;
        std::filesystem::path temporaryPath = std::filesystem::temp_directory_path(temporaryError);
        if (temporaryError) {
            std::cerr << "CEF: cannot resolve the temporary data directory." << std::endl;
            return false;
        }
        g_cef_root = temporaryPath / "UnityCef" / std::to_string(getpid());
        CefString(&settings.root_cache_path) = g_cef_root.string();
#else
        CefMainArgs main_args(GetModuleHandle(NULL));
        settings.multi_threaded_message_loop = true;

        // CEF locks its root; simultaneous game processes need distinct roots.
        std::vector<wchar_t> temporaryPath(32768);
        DWORD length = GetTempPathW(static_cast<DWORD>(temporaryPath.size()), temporaryPath.data());
        if (length == 0 || length >= temporaryPath.size()) {
            std::cerr << "CEF: cannot resolve the temporary data directory." << std::endl;
            return false;
        }
        g_cef_root = std::filesystem::path(std::wstring(temporaryPath.data(), length)) /
            L"UnityCef" / std::to_wstring(GetCurrentProcessId());
        CefString(&settings.root_cache_path) = g_cef_root.wstring();
#endif
        // The log sits beside the root, not in it: the process holds it open until it exits, so the root can go at shutdown.
        std::filesystem::path log = g_cef_root;
        log += ".log";
#ifdef _WIN32
        CefString(&settings.log_file) = log.wstring();
#else
        CefString(&settings.log_file) = log.string();
#endif
        RemoveStaleRoots();

        if (subprocessPath && strlen(subprocessPath) > 0) {
            CefString(&settings.browser_subprocess_path).FromString(subprocessPath);
        }
        if (localesPath && strlen(localesPath) > 0) {
            CefString(&settings.locales_dir_path).FromString(localesPath);
        }
        if (resourcesPath && strlen(resourcesPath) > 0) {
            CefString(&settings.resources_dir_path).FromString(resourcesPath);
        }

        g_cef_initialization_attempted = true;
        bool success = CefInitialize(main_args, settings, new UnityCefApp(), nullptr);
        if (success) {
            g_cef_initialized = true;
        }
        return success;
    }

    UNITY_CEF_EXPORT void ShutdownCEF() {
        if (g_cef_initialized) {
#ifdef __APPLE__
            // Let force-closed browsers finish on this thread before CefShutdown.
            for (int i = 0; i < 20; i++) CefDoMessageLoopWork();
#endif
            CefShutdown();
            g_cef_initialized = false;
            // Chromium keeps a database or two open until the process exits; what remains goes at the next start.
            std::error_code error;
            std::filesystem::remove_all(g_cef_root, error);
        }
    }

    UNITY_CEF_EXPORT void SetFrameRate(int fps) {
        g_frame_rate = fps < 1 ? 1 : fps;
    }

    UNITY_CEF_EXPORT int CreateBrowser(const char* url, int width, int height) {
        if (!g_cef_initialized) return -1;
        BrowsersLock lock(g_browsers_mutex);
        int browserId = g_next_browser_id++;

        CefWindowInfo windowInfo;
        windowInfo.SetAsWindowless(NULL);
#ifdef _WIN32
        windowInfo.shared_texture_enabled = g_d3d12 && !g_shared_texture_failed;
#endif

        CefBrowserSettings browserSettings;
        browserSettings.background_color = 0x00000000; // Transparent background
        // HtmlUiView sets the rate from the display before the first browser.
        browserSettings.windowless_frame_rate = g_frame_rate;

        CefRefPtr<CustomClient> client = new CustomClient(browserId, width, height);

        BrowserInstance instance;
        instance.id = browserId;
        instance.client = client;
        instance.isCreated = false;
        g_browsers[browserId] = instance;

        CefString urlStr(url);
        bool success = CefBrowserHost::CreateBrowser(windowInfo, client, urlStr, browserSettings, nullptr, nullptr);

        if (success) {
            return browserId;
        } else {
            g_browsers.erase(browserId);
            return -1;
        }
    }

    UNITY_CEF_EXPORT void DestroyBrowser(int browserId) {
        BrowsersLock lock(g_browsers_mutex);
        auto it = g_browsers.find(browserId);
        if (it != g_browsers.end()) {
            if (it->second.browser) {
                it->second.browser->GetHost()->CloseBrowser(true);
            }
            g_browsers.erase(it);
        }
    }

    UNITY_CEF_EXPORT void ResizeBrowser(int browserId, int width, int height) {
        BrowsersLock lock(g_browsers_mutex);
        auto it = g_browsers.find(browserId);
        if (it != g_browsers.end()) {
            it->second.client->GetCustomRenderHandler()->Resize(width, height);
            if (it->second.browser) {
                it->second.browser->GetHost()->WasResized();
            }
        }
    }

    UNITY_CEF_EXPORT bool IsBufferDirty(int browserId) {
        BrowsersLock lock(g_browsers_mutex);
        auto it = g_browsers.find(browserId);
        if (it != g_browsers.end()) {
            return it->second.client->GetCustomRenderHandler()->IsDirty();
        }
        return false;
    }

    UNITY_CEF_EXPORT bool CopyTextureBuffer(int browserId, unsigned char* destBuffer, int size, int* outWidth, int* outHeight) {
        BrowsersLock lock(g_browsers_mutex);
        auto it = g_browsers.find(browserId);
        if (it != g_browsers.end()) {
            int w = 0, h = 0;
            bool success = it->second.client->GetCustomRenderHandler()->GetTextureBuffer(destBuffer, size, w, h);
            if (success) {
                if (outWidth) *outWidth = w;
                if (outHeight) *outHeight = h;
                return true;
            }
        }
        return false;
    }

    UNITY_CEF_EXPORT void ExecuteJS(int browserId, const char* code) {
        BrowsersLock lock(g_browsers_mutex);
        auto it = g_browsers.find(browserId);
        if (it != g_browsers.end() && it->second.browser) {
            CefString codeStr(code);
            it->second.browser->GetMainFrame()->ExecuteJavaScript(codeStr, "", 0);
        }
    }

    UNITY_CEF_EXPORT void SendMouseClick(int browserId, int x, int y, int button, int isDown, int clickCount) {
        BrowsersLock lock(g_browsers_mutex);
        auto it = g_browsers.find(browserId);
        if (it != g_browsers.end() && it->second.browser) {
            CefMouseEvent mouse_event;
            mouse_event.x = x;
            mouse_event.y = y;
            CefBrowserHost::MouseButtonType btnType = MBT_LEFT;
            if (button == 1) btnType = MBT_RIGHT;
            if (button == 2) btnType = MBT_MIDDLE;

            it->second.browser->GetHost()->SendMouseClickEvent(mouse_event, btnType, !isDown, clickCount);
        }
    }

    UNITY_CEF_EXPORT void SendMouseMove(int browserId, int x, int y, int mouseLeave) {
        BrowsersLock lock(g_browsers_mutex);
        auto it = g_browsers.find(browserId);
        if (it != g_browsers.end() && it->second.browser) {
            CefMouseEvent mouse_event;
            mouse_event.x = x;
            mouse_event.y = y;
            it->second.browser->GetHost()->SendMouseMoveEvent(mouse_event, mouseLeave != 0);
        }
    }

    UNITY_CEF_EXPORT void SendMouseWheel(int browserId, int x, int y, int deltaX, int deltaY) {
        BrowsersLock lock(g_browsers_mutex);
        auto it = g_browsers.find(browserId);
        if (it != g_browsers.end() && it->second.browser) {
            CefMouseEvent mouse_event;
            mouse_event.x = x;
            mouse_event.y = y;
            it->second.browser->GetHost()->SendMouseWheelEvent(mouse_event, deltaX, deltaY);
        }
    }

    UNITY_CEF_EXPORT void SendKeyEvent(int browserId, int type, int windowsKeyCode, int modifiers) {
        BrowsersLock lock(g_browsers_mutex);
        auto it = g_browsers.find(browserId);
        if (it != g_browsers.end() && it->second.browser) {
            CefKeyEvent key_event;
            key_event.type = (cef_key_event_type_t)type;
            key_event.windows_key_code = windowsKeyCode;
            key_event.modifiers = modifiers;
#ifdef __APPLE__
            if (!TranslateMacKey(key_event)) return;
#endif
            it->second.browser->GetHost()->SendKeyEvent(key_event);
        }
    }

    UNITY_CEF_EXPORT void RegisterCallbacks(MessageCallback messageCallback) {
        g_message_callback = messageCallback;
    }

#ifdef _WIN32
    static void UNITY_INTERFACE_API OnSharedFrameEvent(int eventId, void* data) {
        CefRefPtr<CustomRenderHandler> handler;
        {
            BrowsersLock lock(g_browsers_mutex);
            auto it = g_browsers.find(static_cast<int>(reinterpret_cast<intptr_t>(data)));
            if (it != g_browsers.end()) handler = it->second.client->GetCustomRenderHandler();
        }
        if (handler) handler->Upload();
    }

    // The render-thread copy for CommandBuffer.IssuePluginEventAndData(kSharedFrameEvent, browserId); null while
    // browsers paint on the CPU.
    UNITY_CEF_EXPORT UnityRenderingEventAndData GetSharedFrameEvent() {
        return g_d3d12 && !g_shared_texture_failed ? OnSharedFrameEvent : nullptr;
    }

    UNITY_CEF_EXPORT void SetSharedTarget(int browserId, void* texture) {
        BrowsersLock lock(g_browsers_mutex);
        auto it = g_browsers.find(browserId);
        if (it != g_browsers.end()) it->second.client->GetCustomRenderHandler()->SetSharedTarget(static_cast<ID3D12Resource*>(texture));
    }

    UNITY_CEF_EXPORT int TakeSharedFrame(int browserId) {
        BrowsersLock lock(g_browsers_mutex);
        auto it = g_browsers.find(browserId);
        if (it == g_browsers.end()) return 0;
        bool missed = false;
        int frame = it->second.client->GetCustomRenderHandler()->TakeSharedFrame(missed);
        if (missed && it->second.browser) it->second.browser->GetHost()->Invalidate(PET_VIEW);
        return frame;
    }
#endif

#ifdef __APPLE__
    // One CEF message-loop iteration; macOS has no multi-threaded loop, so the view calls this every frame.
    UNITY_CEF_EXPORT void PumpCEF() {
        if (g_cef_initialized) CefDoMessageLoopWork();
    }
#endif
}
