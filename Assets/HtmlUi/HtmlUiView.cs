using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using UnityEngine.EventSystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Hosts the embedded CEF (Chromium) browser through the native UnityCefPlugin on Windows and Apple Silicon.
/// Creates the browser, brings its frames into a RawImage (shared GPU textures under D3D12, CPU buffers
/// otherwise), and relays pointer/keyboard/scroll input to the page. Surfaces page-to-C#
/// messages via <see cref="OnMessage"/> and tracks whether the pointer is over
/// interactive UI so world clicks can be suppressed.
/// </summary>
[RequireComponent(typeof(RawImage))]
public class HtmlUiView : MonoBehaviour, IPointerMoveHandler, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler, IScrollHandler
{
    /// <summary>Frames the browser has painted into the texture; a diagnostic for the offscreen frame rate.</summary>
    public static int PaintCount { get; private set; }

    /// <summary>The Mac helper apps' base name in Contents/Frameworks; CEF appends " (GPU)", " (Renderer)" and the rest.</summary>
    public const string MacHelperName = "UnityCefHelper";

    private const string DLL_NAME = "UnityCefPlugin";

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibrary(string lpFileName);
#endif

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MessageDelegate(int browserId, IntPtr jsonPtr);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern bool InitCEF(string subprocessPath, string localesPath, string resourcesPath);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ShutdownCEF();

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SetFrameRate(int fps);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int CreateBrowser(string url, int width, int height);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern void DestroyBrowser(int browserId);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ResizeBrowser(int browserId, int width, int height);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern bool IsBufferDirty(int browserId);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern bool CopyTextureBuffer(int browserId, IntPtr destBuffer, int size, out int outWidth, out int outHeight);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ExecuteJS(int browserId, string code);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SendMouseClick(int browserId, int x, int y, int button, int isDown, int clickCount);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SendMouseMove(int browserId, int x, int y, int mouseLeave);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SendMouseWheel(int browserId, int x, int y, int deltaX, int deltaY);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SendKeyEvent(int browserId, int type, int windowsKeyCode, int modifiers);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern void RegisterCallbacks(MessageDelegate messageCallback);

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    // Under D3D12 the bridge hands frames over as GPU textures, copied into ours on the render thread; the event is
    // null while browsers paint on the CPU.
    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr GetSharedFrameEvent();

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SetSharedTarget(int browserId, IntPtr texture);

    /// <returns>1 when a frame arrived, 0 when none did, -1 once the browser can no longer hand frames over.</returns>
    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int TakeSharedFrame(int browserId);

    private const int SharedFrameEvent = 0x4345F001; // the id UnityCefPlugin configures
    private static IntPtr sharedFrameEvent;
    private static CommandBuffer sharedFrameUpload;
    private Texture2D sharedTexture;
    private IntPtr sharedTexturePointer;
#endif

#if UNITY_STANDALONE_OSX && !UNITY_EDITOR
    // macOS has no multi-threaded CEF loop: the page runs on this thread, one pump per frame across every view.
    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern void PumpCEF();
    private static int pumpedFrame = -1;
#endif

    // Static CEF state
    private static bool isCefInitialized = false;
    private static MessageDelegate staticMessageCallback;
    private static bool isWorldPointerBlocked;
    private static readonly System.Collections.Generic.Dictionary<int, HtmlUiView> instances =
        new System.Collections.Generic.Dictionary<int, HtmlUiView>();

    // Instance variables
    [Header("Sizing Settings")]
    [Tooltip("If true, the UI panel will automatically stretch to cover 100% of the screen/canvas and match the screen resolution.")]
    public bool matchScreenSize = true;

    [Tooltip("The manual width of the browser if Match Screen Size is disabled.")]
    public int width = 1024;
    [Tooltip("The manual height of the browser if Match Screen Size is disabled.")]
    public int height = 768;

    private int browserId = -1;
    private string browserUrl;
    private RawImage rawImage;
    private Canvas parentCanvas;
    private Texture2D texture;
    private bool isMouseOver = false;

    public event Action<string> OnMessage;

    private readonly System.Collections.Generic.Queue<string> messageQueue = new System.Collections.Generic.Queue<string>();
    private readonly object messageQueueLock = new object();

    public static bool IsWorldPointerBlocked
    {
        get { return isWorldPointerBlocked; }
    }

    private static string PluginDirectory => Path.Combine(Application.dataPath, "Plugins", "x86_64");

    private static string ToWindowsPath(string path)
    {
        return path.Replace("/", "\\");
    }

    static HtmlUiView()
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        // Add plugins directory to DLL search path so Windows loads libcef.dll and dependencies.
        string pluginDir = ToWindowsPath(PluginDirectory);
        bool success = SetDllDirectory(pluginDir);
        if (!success)
        {
            Debug.LogError("HtmlUiView: Failed to set DLL directory to: " + pluginDir);
        }

        // Explicitly load libcef.dll first so its functions are resolved in memory
        string libcefPath = Path.Combine(pluginDir, "libcef.dll");
        IntPtr libcefHandle = LoadLibrary(libcefPath);
        if (libcefHandle == IntPtr.Zero)
        {
            int errorCode = Marshal.GetLastWin32Error();
            Debug.LogError($"HtmlUiView: Failed to load libcef.dll at path: {libcefPath}. Win32 Error Code: {errorCode}");
        }
        else
        {
            Debug.Log("HtmlUiView: Successfully pre-loaded libcef.dll");
        }

        // Then load our plugin which depends on libcef.dll
        string pluginPath = Path.Combine(pluginDir, "UnityCefPlugin.dll");
        IntPtr pluginHandle = LoadLibrary(pluginPath);
        if (pluginHandle == IntPtr.Zero)
        {
            int errorCode = Marshal.GetLastWin32Error();
            Debug.LogError($"HtmlUiView: Failed to load UnityCefPlugin.dll at path: {pluginPath}. Win32 Error Code: {errorCode}");
        }
        else
        {
            Debug.Log("HtmlUiView: Successfully pre-loaded UnityCefPlugin.dll");
        }
#elif UNITY_STANDALONE_OSX
        // Unity resolves UnityCefPlugin.bundle in Contents/PlugIns; the bridge loads the framework itself.
#else
        Debug.LogWarning("HtmlUiView: Unity CEF runtime is bundled for Windows x86_64 and Apple Silicon only.");
#endif
    }

    private void Awake()
    {
        rawImage = GetComponent<RawImage>();
        parentCanvas = GetComponentInParent<Canvas>();
        rawImage.color = Color.white; // Ensure tint color is white for accurate rendering
        InitializeCefIfRequired();
    }

    private void InitializeCefIfRequired()
    {
        // Always register callback so the native side has the correct C# delegate pointer
        staticMessageCallback = OnNativeMessage;
        RegisterCallbacks(staticMessageCallback);

        if (isCefInitialized) return;

#if UNITY_STANDALONE_OSX && !UNITY_EDITOR
        // The framework carries its own locales and resources.
        string macHelper = Path.Combine(Application.dataPath, "Frameworks", MacHelperName + ".app", "Contents", "MacOS", MacHelperName);
        Debug.Log($"HtmlUiView: Initializing CEF. Subprocess: {macHelper}");
        isCefInitialized = InitCEF(macHelper, "", "");
        if (isCefInitialized) Debug.Log("HtmlUiView: CEF initialized successfully.");
        else Debug.LogError("HtmlUiView: CEF initialization failed!");
#else
        string pluginDir = PluginDirectory;
        string subprocessPath = Path.Combine(pluginDir, "UnityCefHelper.exe");
        string localesPath = Path.Combine(pluginDir, "locales");
        string resourcesPath = pluginDir;

        subprocessPath = subprocessPath.Replace("/", "\\");
        localesPath = localesPath.Replace("/", "\\");
        resourcesPath = resourcesPath.Replace("/", "\\");

        Debug.Log($"HtmlUiView: Initializing CEF. Subprocess: {subprocessPath}");

        bool success = InitCEF(subprocessPath, localesPath, resourcesPath);
        if (success)
        {
            isCefInitialized = true;
            Debug.Log("HtmlUiView: CEF initialized successfully.");
        }
        else
        {
            Debug.LogError("HtmlUiView: CEF initialization failed!");
        }
#endif
    }

    [AOT.MonoPInvokeCallback(typeof(MessageDelegate))]
    private static void OnNativeMessage(int browserId, IntPtr jsonPtr)
    {
        string json = Marshal.PtrToStringAnsi(jsonPtr);
        HtmlUiView instance;
        lock (instances)
        {
            if (instances.TryGetValue(browserId, out instance))
            {
                instance.DispatchMessage(json);
            }
        }
    }

    private void DispatchMessage(string json)
    {
        UpdateWorldPointerBlockState(json);

        lock (messageQueueLock)
        {
            messageQueue.Enqueue(json);
        }
    }

    private static void UpdateWorldPointerBlockState(string json)
    {
        if (string.IsNullOrEmpty(json) || !json.Contains("\"type\":\"html-pointer-block\""))
        {
            return;
        }

        try
        {
            HtmlPointerBlockMessage message = JsonUtility.FromJson<HtmlPointerBlockMessage>(json);
            if (message != null && message.type == "html-pointer-block")
            {
                isWorldPointerBlocked = message.blocking;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("HtmlUiView: Failed to parse pointer block message: " + ex.Message);
        }
    }

    private void Start()
    {
        // Transparent until the page paints, rather than the RawImage's white.
        Vector2Int targetSize = ResolveBrowserSize();
        EnsureTexture(targetSize.x, targetSize.y);
    }

    public void LoadStreamingAsset(string relativePath)
    {
        string fullPath = Path.Combine(Application.streamingAssetsPath, relativePath);
        fullPath = fullPath.Replace("\\", "/");
        // A Mac path already starts with its root slash.
        string url = (fullPath.StartsWith("/") ? "file://" : "file:///") + fullPath;

        if (browserId != -1)
        {
            DestroyBrowserInstance();
        }

        Vector2Int targetSize = ResolveBrowserSize();
        EnsureTexture(targetSize.x, targetSize.y);

        browserUrl = url;
        CreateBrowserInstance();
    }

    private void CreateBrowserInstance()
    {
        // The page paints at the display's refresh within 120-144, so page motion that follows the world (labels
        // over moving objects) trails the camera less.
        int paintRate = Mathf.Clamp(Mathf.RoundToInt((float)Screen.currentResolution.refreshRateRatio.value), 120, 144);
        SetFrameRate(paintRate);
        browserId = CreateBrowser(browserUrl, width, height);
        if (browserId != -1)
        {
            lock (instances)
            {
                instances[browserId] = this;
            }
            Debug.Log($"HtmlUiView: Created browser {browserId} at {paintRate} paints/s{(SharesGpuFrames() ? " on the GPU" : "")} for URL: {browserUrl}");
        }
        else
        {
            Debug.LogError("HtmlUiView: Failed to create browser!");
        }
    }

    public void SendToJavaScript(string json)
    {
        if (browserId == -1) return;

        string encodedJson = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        string code = $"(() => {{ const b = '{encodedJson}'; const bytes = Uint8Array.from(atob(b), c => c.charCodeAt(0)); window.UnityBridge && window.UnityBridge._receive(new TextDecoder('utf-8').decode(bytes)); }})();";
        ExecuteJS(browserId, code);
    }

    private void Update()
    {
#if UNITY_STANDALONE_OSX && !UNITY_EDITOR
        if (pumpedFrame != Time.frameCount) { pumpedFrame = Time.frameCount; PumpCEF(); }
#endif
        if (browserId == -1) return;

        // Process queued messages on the main Unity thread
        lock (messageQueueLock)
        {
            while (messageQueue.Count > 0)
            {
                string json = messageQueue.Dequeue();
                OnMessage?.Invoke(json);
            }
        }

        Vector2Int targetSize = ResolveBrowserSize();
        if (targetSize.x != width || targetSize.y != height)
        {
            ResizeBrowser(browserId, targetSize.x, targetSize.y);
            EnsureTexture(targetSize.x, targetSize.y);
            Debug.Log($"HtmlUiView: Resized browser to {width}x{height}.");
        }

        if (TakeGpuFrame()) PaintCount++;
        else if (IsBufferDirty(browserId))
        {
            // The frame lands straight in the texture's own pixels, one copy from the browser to the upload.
            var pixels = texture.GetRawTextureData<byte>();
            bool copied;
            unsafe { copied = CopyTextureBuffer(browserId, (IntPtr)NativeArrayUnsafeUtility.GetUnsafePtr(pixels), pixels.Length, out int outW, out int outH) && outW == width && outH == height; }
            if (copied)
            {
                texture.Apply(false);
                PaintCount++;
                ForgetSharedTexture();
            }
        }

        // Forward keyboard input if pointer is hovering over the panel
        if (isMouseOver)
        {
            HandleKeyboardInput();
        }
    }

    private static bool SharesGpuFrames()
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        // Kept from the browser that opened shared: a later failure elsewhere nulls the export, not its frames.
        IntPtr frameEvent = GetSharedFrameEvent();
        if (frameEvent != IntPtr.Zero) sharedFrameEvent = frameEvent;
        return frameEvent != IntPtr.Zero;
#else
        return false;
#endif
    }

    // Apply replaces the texture's native resource, so a GPU frame after a CPU one asks for the pointer again.
    private void ForgetSharedTexture()
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        sharedTexture = null;
#endif
    }

    private bool TakeGpuFrame()
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        int frame = TakeSharedFrame(browserId);
        if (frame < 0)
        {
            // At the first frame, before the page holds any state: browsers from here on paint on the CPU.
            Debug.LogWarning("HtmlUiView: The GPU frame hand-off failed; reopening the page with CPU paints.");
            DestroyBrowserInstance();
            if (browserUrl != null) CreateBrowserInstance();
            return false;
        }
        if (frame == 0) return false;
        // Asking for the pointer waits for the render thread, so only a new texture asks.
        if (sharedTexture != texture) { sharedTexture = texture; sharedTexturePointer = texture.GetNativeTexturePtr(); }
        SetSharedTarget(browserId, sharedTexturePointer);
        sharedFrameUpload ??= new CommandBuffer { name = "HtmlUiView GPU frame" };
        sharedFrameUpload.Clear();
        sharedFrameUpload.IssuePluginEventAndData(sharedFrameEvent, SharedFrameEvent, (IntPtr)browserId);
        Graphics.ExecuteCommandBuffer(sharedFrameUpload);
        return true;
#else
        return false;
#endif
    }

#if ENABLE_INPUT_SYSTEM
    private void OnEnable()
    {
        if (Keyboard.current != null)
        {
            Keyboard.current.onTextInput += OnTextInput;
        }
    }

    private void OnTextInput(char c)
    {
        if (browserId != -1 && isMouseOver)
        {
            if (c == '\t') return;
            SendKeyEvent(browserId, 3, (int)c, 0); // 3 = KEYEVENT_CHAR
        }
    }

    private int GetModifiers()
    {
        int modifiers = 0;
        var keyboard = Keyboard.current;
        if (keyboard != null)
        {
            if (keyboard.shiftKey.isPressed) modifiers |= 1 << 0; // EVENTFLAG_SHIFT_DOWN
            if (keyboard.ctrlKey.isPressed) modifiers |= 1 << 2;  // EVENTFLAG_CONTROL_DOWN
            if (keyboard.altKey.isPressed) modifiers |= 1 << 3;   // EVENTFLAG_ALT_DOWN
        }
        return modifiers;
    }

    private void HandleKeyboardInput()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null) return;

        ForwardSpecialKeyNew(keyboard.backspaceKey, 0x08);
        ForwardSpecialKeyNew(keyboard.enterKey, 0x0D);
        ForwardSpecialKeyNew(keyboard.deleteKey, 0x2E);
        ForwardSpecialKeyNew(keyboard.tabKey, 0x09);
        ForwardSpecialKeyNew(keyboard.leftArrowKey, 0x25);
        ForwardSpecialKeyNew(keyboard.rightArrowKey, 0x27);
        ForwardSpecialKeyNew(keyboard.upArrowKey, 0x26);
        ForwardSpecialKeyNew(keyboard.downArrowKey, 0x28);
        ForwardSpecialKeyNew(keyboard.escapeKey, 0x1B);
        ForwardSpecialKeyNew(keyboard.shiftKey, 0x10);
        ForwardSpecialKeyNew(keyboard.backslashKey, 0xDC);
    }

    private void ForwardSpecialKeyNew(UnityEngine.InputSystem.Controls.ButtonControl keyControl, int windowsKeyCode)
    {
        int modifiers = GetModifiers();
        if (keyControl.wasPressedThisFrame)
        {
            SendKeyEvent(browserId, 0, windowsKeyCode, modifiers); // 0 = KEYEVENT_RAWKEYDOWN
        }
        if (keyControl.wasReleasedThisFrame)
        {
            SendKeyEvent(browserId, 2, windowsKeyCode, modifiers); // 2 = KEYEVENT_KEYUP
        }
    }
#else
    private int GetModifiers()
    {
        int modifiers = 0;
        if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) modifiers |= 1 << 0;
        if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) modifiers |= 1 << 2;
        if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)) modifiers |= 1 << 3;
        return modifiers;
    }

    private void HandleKeyboardInput()
    {
        string chars = Input.inputString;
        foreach (char c in chars)
        {
            if (c == '\t') continue;
            SendKeyEvent(browserId, 3, (int)c, 0); // 3 = KEYEVENT_CHAR
        }

        ForwardSpecialKey(KeyCode.Backspace, 0x08);
        ForwardSpecialKey(KeyCode.Return, 0x0D);
        ForwardSpecialKey(KeyCode.Delete, 0x2E);
        ForwardSpecialKey(KeyCode.Tab, 0x09);
        ForwardSpecialKey(KeyCode.LeftArrow, 0x25);
        ForwardSpecialKey(KeyCode.RightArrow, 0x27);
        ForwardSpecialKey(KeyCode.UpArrow, 0x26);
        ForwardSpecialKey(KeyCode.DownArrow, 0x28);
        ForwardSpecialKey(KeyCode.Escape, 0x1B);
        ForwardSpecialKey(KeyCode.LeftShift, 0x10);
        ForwardSpecialKey(KeyCode.RightShift, 0x10);
        ForwardSpecialKey(KeyCode.Backslash, 0xDC);
    }

    private void ForwardSpecialKey(KeyCode keyCode, int windowsKeyCode)
    {
        int modifiers = GetModifiers();
        if (Input.GetKeyDown(keyCode))
        {
            SendKeyEvent(browserId, 0, windowsKeyCode, modifiers); // 0 = KEYEVENT_RAWKEYDOWN
        }
        if (Input.GetKeyUp(keyCode))
        {
            SendKeyEvent(browserId, 2, windowsKeyCode, modifiers); // 2 = KEYEVENT_KEYUP
        }
    }
#endif

    private void OnDestroy()
    {
        DestroyBrowserInstance();
        if (texture != null) Destroy(texture);
    }

    private void DestroyBrowserInstance()
    {
        if (browserId != -1)
        {
            lock (instances)
            {
                instances.Remove(browserId);
            }
            DestroyBrowser(browserId);
            browserId = -1;
        }
    }

    private Vector2Int ResolveBrowserSize()
    {
        if (matchScreenSize)
        {
            StretchToScreen();
        }

        RectTransform rt = rawImage != null ? rawImage.rectTransform : GetComponent<RectTransform>();
        int targetW = 0;
        int targetH = 0;

        if (rt != null)
        {
            Rect rect = rt.rect;
            targetW = Mathf.RoundToInt(rect.width);
            targetH = Mathf.RoundToInt(rect.height);
        }

        Canvas canvas = GetParentCanvas();
        if (canvas != null)
        {
            if (matchScreenSize && canvas.pixelRect.width > 0f && canvas.pixelRect.height > 0f)
            {
                targetW = Mathf.RoundToInt(canvas.pixelRect.width);
                targetH = Mathf.RoundToInt(canvas.pixelRect.height);
            }
            else if (canvas.scaleFactor > 0f && targetW > 0 && targetH > 0)
            {
                targetW = Mathf.RoundToInt(targetW * canvas.scaleFactor);
                targetH = Mathf.RoundToInt(targetH * canvas.scaleFactor);
            }
        }

        if (targetW <= 0 || targetH <= 0)
        {
            targetW = matchScreenSize ? Screen.width : width;
            targetH = matchScreenSize ? Screen.height : height;
        }

        return new Vector2Int(Mathf.Max(1, targetW), Mathf.Max(1, targetH));
    }

    private Canvas GetParentCanvas()
    {
        if (parentCanvas == null)
        {
            parentCanvas = GetComponentInParent<Canvas>();
        }

        return parentCanvas;
    }

    private void StretchToScreen()
    {
        RectTransform rt = rawImage != null ? rawImage.rectTransform : GetComponent<RectTransform>();
        if (rt == null)
        {
            return;
        }

        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    private void EnsureTexture(int targetW, int targetH)
    {
        if (texture != null && width == targetW && height == targetH && rawImage.texture == texture)
        {
            return;
        }

        width = targetW;
        height = targetH;

        if (texture != null)
        {
            Destroy(texture);
        }

        texture = new Texture2D(width, height, TextureFormat.BGRA32, false);
        texture.filterMode = FilterMode.Bilinear;
        texture.wrapMode = TextureWrapMode.Clamp;
        rawImage.texture = texture;
        // Flip Y UV coordinate because CEF's top-left pixel mapping is upside down in Unity's bottom-left texture space
        rawImage.uvRect = new Rect(0, 1, 1, -1);

        var pixels = texture.GetRawTextureData<byte>();
        unsafe { UnsafeUtility.MemClear(NativeArrayUnsafeUtility.GetUnsafePtr(pixels), pixels.Length); }
        texture.Apply(false);
    }

    private void OnApplicationQuit()
    {
        DestroyBrowserInstance();
        // In the Unity Editor, we must not call ShutdownCEF() on play mode stop,
        // because CEF cannot be re-initialized in the same process.
        // Standalone builds can shutdown CEF normally as the process terminates.
#if !UNITY_EDITOR
        if (isCefInitialized)
        {
            lock (instances)
            {
                instances.Clear();
            }
            ShutdownCEF();
            isCefInitialized = false;
        }
#else
        lock (instances)
        {
            instances.Clear();
        }
#endif
    }

    private Vector2 ConvertToHtmlSpace(Vector2 screenPosition, Camera eventCamera)
    {
        Vector2 localPoint;
        // Check local hit relative to RawImage transform
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(rawImage.rectTransform, screenPosition, eventCamera, out localPoint))
        {
            float rectWidth = rawImage.rectTransform.rect.width;
            float rectHeight = rawImage.rectTransform.rect.height;
            if (rectWidth <= 0 || rectHeight <= 0) return Vector2.zero;

            float xNormal = (localPoint.x - rawImage.rectTransform.rect.x) / rectWidth;
            float yNormal = (localPoint.y - rawImage.rectTransform.rect.y) / rectHeight;

            int x = Mathf.Clamp((int)(xNormal * width), 0, width - 1);
            int y = Mathf.Clamp((int)((1f - yNormal) * height), 0, height - 1); // Flip Y coordinate
            return new Vector2(x, y);
        }
        return Vector2.zero;
    }

    public void OnPointerMove(PointerEventData eventData)
    {
        if (browserId == -1) return;
        isMouseOver = true;
        Vector2 pt = ConvertToHtmlSpace(eventData.position, eventData.enterEventCamera);
        SendMouseMove(browserId, (int)pt.x, (int)pt.y, 0);
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        if (browserId == -1) return;
        isMouseOver = false;
        Vector2 pt = ConvertToHtmlSpace(eventData.position, eventData.enterEventCamera);
        SendMouseMove(browserId, (int)pt.x, (int)pt.y, 1);
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        if (browserId == -1) return;
        Vector2 pt = ConvertToHtmlSpace(eventData.position, eventData.pressEventCamera);

        int button = 0; // 0 = Left, 1 = Right, 2 = Middle
        if (eventData.button == PointerEventData.InputButton.Right) button = 1;
        if (eventData.button == PointerEventData.InputButton.Middle) button = 2;

        SendMouseClick(browserId, (int)pt.x, (int)pt.y, button, 1, 1);
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        if (browserId == -1) return;
        Vector2 pt = ConvertToHtmlSpace(eventData.position, eventData.pressEventCamera);

        int button = 0;
        if (eventData.button == PointerEventData.InputButton.Right) button = 1;
        if (eventData.button == PointerEventData.InputButton.Middle) button = 2;

        SendMouseClick(browserId, (int)pt.x, (int)pt.y, button, 0, 1);
    }

    public void OnScroll(PointerEventData eventData)
    {
        if (browserId == -1) return;
        Vector2 pt = ConvertToHtmlSpace(eventData.position, eventData.enterEventCamera);
        int deltaX = Mathf.RoundToInt(eventData.scrollDelta.x * 20);
        int deltaY = Mathf.RoundToInt(eventData.scrollDelta.y * 20);
        SendMouseWheel(browserId, (int)pt.x, (int)pt.y, deltaX, deltaY);
    }

    // Toggle mouse hover state to disable keyboard trapping when moving mouse away
    private void OnDisable()
    {
        isMouseOver = false;
        isWorldPointerBlocked = false;
#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current != null)
        {
            Keyboard.current.onTextInput -= OnTextInput;
        }
#endif
    }

    [Serializable]
    private sealed class HtmlPointerBlockMessage
    {
        public string type;
        public bool blocking;
    }
}
