using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace WitchSpring3Resolution
{
    [BepInPlugin(Id, "WitchSpring3 Adaptive Resolution", "1.0.1")]
    public sealed class ResolutionPlugin : BaseUnityPlugin
    {
        public const string Id = "com.dsh.witchspring3.resolution";
        internal static ResolutionPlugin Instance;
        private Harmony harmony;
        private ConfigEntry<bool> enabledEntry, nativeStartup, expandWorld;
        private ConfigEntry<int> widthEntry, heightEntry;
        private int startupWidth, startupHeight, seenWidth, seenHeight;
        private int windowWidth = 1920, windowHeight = 1080;
        private FullScreenMode seenMode, startupMode;
        private float changedAt, pollAt, startupAt;
        private bool startupPending = true, dirty = true, busy, titleFixed;
        private Camera directCamera, outputCamera;
        private RawImage outputImage;
        private RenderTexture originalTexture;
        private Rect originalRect;
        private bool originalOutputEnabled, originalImageEnabled;
        private Camera borderCamera;
        private readonly Dictionary<Camera, Rect> uiCameraRects = new Dictionary<Camera, Rect>();
        private readonly Dictionary<RectTransform, Vector2> adjusted = new Dictionary<RectTransform, Vector2>();
        private readonly float[] frameMs = new float[65536];
        private int frameCount, frameIndex;
        private float transitionMaxMs;
        private long privateStart;
        private string reportDir;
        private static readonly CultureInfo CI = CultureInfo.InvariantCulture;

        private void Awake()
        {
            Instance = this;
            enabledEntry = Config.Bind("General", "Enabled", true, "Enable adaptive resolution; restart after changing.");
            nativeStartup = Config.Bind("General", "NativeAtStartup", true, "Use desktop resolution once at startup; explicit -screen-width/-screen-height take priority.");
            widthEntry = Config.Bind("General", "StartupWidth", 0, "0 = automatic; positive width and height override native desktop size.");
            heightEntry = Config.Bind("General", "StartupHeight", 0, "0 = automatic.");
            expandWorld = Config.Bind("General", "ExpandWorld", true, "True = fill ultrawide with a wider view, including the original scene boundaries (default, requested). False = optional 16:9 composition with black borders. Restart after changing.");
            if (!enabledEntry.Value) return;
            var background = new GameObject("AdaptiveResolutionBlackBackground");
            DontDestroyOnLoad(background);
            borderCamera = background.AddComponent<Camera>();
            borderCamera.depth = -10000f;
            borderCamera.clearFlags = CameraClearFlags.SolidColor;
            borderCamera.backgroundColor = Color.black;
            borderCamera.cullingMask = 0;
            borderCamera.enabled = false;
            startupMode = Screen.fullScreenMode;
            Vector2Int desktop = NativeDesktop();
            startupWidth = desktop.x;
            startupHeight = desktop.y;
            if (startupMode == FullScreenMode.Windowed) { windowWidth = Screen.width; windowHeight = Screen.height; }
            if (widthEntry.Value > 0 && heightEntry.Value > 0)
            { startupWidth = widthEntry.Value; startupHeight = heightEntry.Value; }
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
            {
                int value;
                if (args[i] == "-screen-width" && int.TryParse(args[i + 1], out value)) startupWidth = value;
                if (args[i] == "-screen-height" && int.TryParse(args[i + 1], out value)) startupHeight = value;
            }
            reportDir = Path.Combine(Paths.GameRootPath, "ModDev", "resolution-reports");
            Directory.CreateDirectory(reportDir);
            harmony = new Harmony(Id);
            harmony.PatchAll(typeof(ResolutionPlugin).Assembly);
            SceneManager.sceneLoaded += SceneLoaded;
            startupAt = Time.realtimeSinceStartup + 1f;
            Logger.LogInfo("Adaptive resolution 1.0.1 ready. Fullscreen uses native borderless output. F8 preserves display mode; F9 audits current output. Command file: " + Path.Combine(reportDir, "request.txt"));
        }

        private void SceneLoaded(Scene scene, LoadSceneMode mode) { titleFixed = false; MarkDirty(); }
        internal void MarkDirty() { dirty = true; changedAt = Time.realtimeSinceStartup; }

        private void LateUpdate()
        {
            if (!enabledEntry.Value || titleFixed || SceneManager.GetActiveScene().name != "Title") return;
            var title = MonoBehaviourSingleton<SwitchTitleController>.instance;
            if (title == null || title.MainButtons.Count != 4) return;
            foreach (Button button in title.MainButtons) if (button == null || !button.interactable) return;
            // The authored menu is one group: Start=74, Diary=32, Continue=-10,
            // Exit=-51.3. Never inset Start independently during its intro animation.
            RectTransform diary = title.MainButtons[1].GetComponent<RectTransform>();
            RectTransform next = title.MainButtons[2].GetComponent<RectTransform>();
            RectTransform start = title.MainButtons[0].GetComponent<RectTransform>();
            start.anchoredPosition = diary.anchoredPosition + (diary.anchoredPosition - next.anchoredPosition);
            int selected = (int)AccessTools.Field(typeof(SwitchTitleController), "mCurrNumber").GetValue(title);
            if (title.SelectionCursor != null && selected >= 0 && selected < 4)
                title.SelectionCursor.anchoredPosition = title.MainButtons[selected].GetComponent<RectTransform>().anchoredPosition;
            titleFixed = true;
            Logger.LogInfo("Title menu grouped after intro: Start/Diary/Continue/Exit; first row pitch=" + (diary.anchoredPosition.y - next.anchoredPosition.y));
        }

        private void Update()
        {
            if (!enabledEntry.Value) return;
            frameMs[frameIndex] = Time.unscaledDeltaTime * 1000f;
            frameIndex = (frameIndex + 1) % frameMs.Length;
            frameCount = Math.Min(frameCount + 1, frameMs.Length);
            float now = Time.realtimeSinceStartup;
            // DataSet.Awake contains a 1920-width cap. Apply once after it has initialized.
            if (startupPending && MonoBehaviourSingleton<DataSet>.instance != null && now >= startupAt)
            {
                startupPending = false;
                if (nativeStartup.Value || widthEntry.Value > 0 || HasScreenArguments())
                {
                    if (startupMode != FullScreenMode.Windowed) EnterFullscreen();
                    else Screen.SetResolution(Mathf.Clamp(startupWidth, 640, 7680), Mathf.Clamp(startupHeight, 360, 4320), FullScreenMode.Windowed);
                }
                MarkDirty();
            }
            if (Screen.width != seenWidth || Screen.height != seenHeight || Screen.fullScreenMode != seenMode)
            {
                seenWidth = Screen.width; seenHeight = Screen.height; seenMode = Screen.fullScreenMode;
                if (seenMode == FullScreenMode.Windowed) { windowWidth = seenWidth; windowHeight = seenHeight; }
                MarkDirty();
            }
            if (dirty && now - changedAt >= 0.2f && Screen.width > 0 && Screen.height > 0)
            { dirty = false; ApplyViewport(); }
            if (Input.GetKeyDown(KeyCode.F8) && !busy)
            {
                int[,] sizes = { {3440,1440}, {2560,1440}, {1920,1080}, {1366,768} };
                int next = 0;
                for (int i = 0; i < 4; i++) if (Screen.width == sizes[i,0] && Screen.height == sizes[i,1]) next = (i + 1) % 4;
                StartCoroutine(ValidateSize(sizes[next,0], sizes[next,1]));
            }
            if (Input.GetKeyDown(KeyCode.F9) && !busy) StartCoroutine(ValidateSize(0, 0));
            if (now >= pollAt && !busy)
            {
                pollAt = now + 1f;
                string request = Path.Combine(reportDir, "request.txt");
                if (File.Exists(request))
                {
                    try
                    {
                        string[] parts = File.ReadAllText(request).Trim().Split(new[] {' '}, StringSplitOptions.RemoveEmptyEntries);
                        File.Delete(request);
                        int w, h;
                        if ((parts.Length == 2 || (parts.Length == 3 && (parts[2] == "windowed" || parts[2] == "fullscreen"))) && int.TryParse(parts[0], out w) && int.TryParse(parts[1], out h) &&
                            ((w == 3440 && h == 1440) || (w == 2560 && h == 1440) || (w == 1920 && h == 1080) || (w == 1366 && h == 768) || (w == 0 && h == 0)))
                            StartCoroutine(ValidateSize(w, h, parts.Length == 3 ? parts[2] : "current"));
                        else Logger.LogWarning("Invalid audit request; use one of the four documented resolutions, or 0 0.");
                    }
                    catch (IOException e) { Logger.LogWarning(e.Message); }
                }
            }
        }

        private static bool HasScreenArguments()
        { return Array.IndexOf(Environment.GetCommandLineArgs(), "-screen-width") >= 0; }

        internal bool Active { get { return enabledEntry.Value; } }

        internal void AfterDataAwake(Vector3Int previous)
        {
            // DataSet can initialize again on entering gameplay, after the title
            // startup correction. Undo its deferred 1920 cap for every instance.
            if (Active && previous.x > 1920) StartCoroutine(RestoreAfterDataAwake(previous));
        }
        private IEnumerator RestoreAfterDataAwake(Vector3Int previous)
        {
            yield return null;
            if ((FullScreenMode)previous.z != FullScreenMode.Windowed) EnterFullscreen();
            else Screen.SetResolution(previous.x, previous.y, FullScreenMode.Windowed);
            MarkDirty();
        }

        // Read the configured desktop mode, rather than a legacy exclusive game's
        // current 16:9 mode. Monitor device selection follows the game's HWND.
        private static Vector2Int NativeDesktop()
        {
            try
            {
                IntPtr hwnd;
                using (var p = System.Diagnostics.Process.GetCurrentProcess()) hwnd = p.MainWindowHandle;
                var info = new MonitorInfo { size = Marshal.SizeOf(typeof(MonitorInfo)) };
                if (GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info))
                {
                    var mode = new DesktopMode { size = (short)Marshal.SizeOf(typeof(DesktopMode)) };
                    if (EnumDisplaySettings(info.device, -2, ref mode) && mode.width > 0 && mode.height > 0)
                        return new Vector2Int(mode.width, mode.height);
                }
            }
            catch (Exception) { /* Unity fallback on non-Windows or unavailable HWND. */ }
            return new Vector2Int(Display.main.systemWidth, Display.main.systemHeight);
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MonitorInfo
        {
            public int size;
            public int left, top, right, bottom, workLeft, workTop, workRight, workBottom, flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string device;
        }
        [StructLayout(LayoutKind.Explicit, Size = 220, CharSet = CharSet.Unicode)]
        private struct DesktopMode
        {
            [FieldOffset(68)] public short size;
            [FieldOffset(172)] public int width;
            [FieldOffset(176)] public int height;
        }
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string device, int number, ref DesktopMode mode);

        private void EnterFullscreen()
        {
            if (Screen.fullScreenMode == FullScreenMode.Windowed)
            { windowWidth = Screen.width; windowHeight = Screen.height; }
            Vector2Int desktop = NativeDesktop();
            Screen.SetResolution(desktop.x, desktop.y, FullScreenMode.FullScreenWindow);
            AccessTools.PropertySetter(typeof(DataSet), "DisplayType").Invoke(null, new object[] { eDisplay.FullScreen });
            MarkDirty();
        }

        internal bool ChangeDisplay(eDisplay display)
        {
            if (!Active) return true;
            AccessTools.PropertySetter(typeof(DataSet), "DisplayType").Invoke(null, new object[] { display });
            if (display == eDisplay.FullScreen) EnterFullscreen();
            else Screen.SetResolution(windowWidth, windowHeight, FullScreenMode.Windowed);
            Application.targetFrameRate = 60;
            MarkDirty();
            return false;
        }

        internal bool ChangeResolution(eResolution resolution)
        {
            if (!Active) return true;
            AccessTools.PropertySetter(typeof(DataSet), "ResolutionType").Invoke(null, new object[] { resolution });
            Resolution size = DataSet.GetResolution(resolution);
            if (Screen.fullScreenMode == FullScreenMode.Windowed)
                Screen.SetResolution(size.width, size.height, FullScreenMode.Windowed);
            else
            {
                // Legacy choices become the next window size; they must not put
                // a 16:9 backbuffer inside a 16:10/ultrawide fullscreen window.
                windowWidth = size.width; windowHeight = size.height;
                EnterFullscreen();
            }
            Application.targetFrameRate = 60;
            MarkDirty();
            return false;
        }

        internal bool ChangeResolution(Resolution size, FullScreenMode mode)
        {
            if (!Active) return true;
            if (mode != FullScreenMode.Windowed) EnterFullscreen();
            else
            {
                AccessTools.PropertySetter(typeof(DataSet), "DisplayType").Invoke(null, new object[] { eDisplay.Window });
                Screen.SetResolution(size.width, size.height, mode);
            }
            MarkDirty();
            return false;
        }

        internal void UseDirectCamera(UIScreen screen)
        {
            if (!enabledEntry.Value || screen == null) return;
            Camera camera = screen.GetTargetCamera();
            if (camera == null) return;
            if (directCamera != camera)
            {
                RestoreOutput();
                directCamera = camera;
                originalTexture = camera.targetTexture;
                originalRect = camera.rect;
                outputImage = AccessTools.Field(typeof(UIScreen), "rawImage").GetValue(screen) as RawImage;
                outputCamera = AccessTools.Field(typeof(UIScreen), "uiScreenCamera").GetValue(screen) as Camera;
                if (outputImage != null) originalImageEnabled = outputImage.enabled;
                if (outputCamera != null) originalOutputEnabled = outputCamera.enabled;
            }
            // Preserve world projection and hit tests in native screen coordinates.
            // Do not allocate a replacement render texture on resize.
            camera.targetTexture = null;
            camera.rect = ContentViewport();
            camera.ResetAspect();
            if (outputImage != null) outputImage.enabled = false;
            if (outputCamera != null) outputCamera.enabled = false;
            MarkDirty();
        }

        internal bool IsDirect(Camera camera)
        { return enabledEntry.Value && camera != null && camera == directCamera && camera.targetTexture == null; }

        private Rect ContentViewport()
        {
            float aspect = (float)Screen.width / Mathf.Max(1, Screen.height);
            const float design = 16f / 9f;
            if (expandWorld.Value || Mathf.Abs(aspect / design - 1f) <= 0.01f) return new Rect(0,0,1,1);
            if (aspect > design) { float w = design / aspect; return new Rect((1f-w)/2f,0,w,1); }
            float h = aspect / design; return new Rect(0,(1f-h)/2f,1,h);
        }

        private void ApplyViewport()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var stale = new List<RectTransform>();
            foreach (var pair in adjusted)
            {
                if (pair.Key == null) stale.Add(pair.Key);
                else pair.Key.anchoredPosition = pair.Value;
            }
            foreach (var key in stale) adjusted.Remove(key);
            Rect viewport = ContentViewport();
            borderCamera.enabled = viewport.width < 0.999f || viewport.height < 0.999f;
            if (directCamera != null)
            {
                directCamera.rect = viewport;
                directCamera.ResetAspect();
            }
            else if (Camera.main != null && Camera.main.targetTexture == null)
            {
                if (!uiCameraRects.ContainsKey(Camera.main)) uiCameraRects.Add(Camera.main, Camera.main.rect);
                Camera.main.rect = viewport; Camera.main.ResetAspect();
            }
            var staleCameras = new List<Camera>();
            foreach (var pair in uiCameraRects) if (pair.Key == null) staleCameras.Add(pair.Key);
            foreach (var key in staleCameras) uiCameraRects.Remove(key);
            foreach (Canvas canvas in Resources.FindObjectsOfTypeAll<Canvas>())
            {
                if (!canvas.gameObject.scene.IsValid() || !canvas.isRootCanvas || canvas.renderMode != RenderMode.ScreenSpaceCamera) continue;
                Camera ui = canvas.worldCamera;
                if (ui == null || ui == directCamera || ui == Camera.main || ui == outputCamera || ui.targetTexture != null) continue;
                if (!uiCameraRects.ContainsKey(ui)) uiCameraRects.Add(ui, ui.rect);
                ui.rect = viewport; ui.ResetAspect();
            }
            // Main canvases already use 800x600, MatchHeight=1. Keep authored anchors
            // and scale intact, including UIOperator's Screen.height/600 effects.
            Canvas.ForceUpdateCanvases();
            foreach (PlayController play in Resources.FindObjectsOfTypeAll<PlayController>())
            {
                if (!play.gameObject.scene.IsValid()) continue;
                AccessTools.Field(typeof(PlayController), "halfScreenWidth").SetValue(play, Screen.width / 2f);
                AccessTools.Field(typeof(PlayController), "halfScreenHeight").SetValue(play, Screen.height / 2f);
                AccessTools.Field(typeof(PlayController), "multiRatio").SetValue(play, Screen.height / 600f);
                var data = MonoBehaviourSingleton<DataSet>.instance;
                if (data != null && data.ui != null && data.ui.canvas != null)
                {
                    RectTransform root = play.GetComponent<RectTransform>();
                    // SetPlayUI resets these after SetStart: this prefab stretches
                    // to its parent. Copying the canvas size would double its size.
                    if (root.anchorMin == Vector2.zero && root.anchorMax == Vector2.one)
                    { root.offsetMin = Vector2.zero; root.offsetMax = Vector2.zero; }
                }
            }
            InsetKnownControls();
            watch.Stop();
            Logger.LogInfo(string.Format(CI, "Viewport {0}x{1}; world aspect={2:F6}; UI height scale={3:F4}; apply={4:F3}ms",
                Screen.width, Screen.height, (float)Screen.width / Screen.height, Screen.height / 600f, watch.Elapsed.TotalMilliseconds));
        }

        private static string Category(RectTransform rt)
        {
            var data = MonoBehaviourSingleton<DataSet>.instance;
            if (data != null && data.ui != null && data.ui.playUI != null && data.ui.playUI.fieldMenuButton == rt.gameObject) return "Primary";
            switch (rt.name.ToLowerInvariant())
            {
                case "button_close": case "button_back": case "button_cancel": return "Back";
                case "button_ok": case "button_start": case "button_attack": case "fieldmenubutton": return "Primary";
                case "hpmptextpanel": case "hpbar": case "mpbar": case "playerhpbar": case "playermpbar": return "Status";
                default: return "";
            }
        }

        private void InsetKnownControls()
        {
            // Exact semantic allowlist only. Never move scrolling entries, backgrounds,
            // cinematic masks or world-space labels based on a fuzzy name match.
            foreach (RectTransform rt in Resources.FindObjectsOfTypeAll<RectTransform>())
            {
                if (!rt.gameObject.scene.IsValid() || !rt.gameObject.activeInHierarchy || Category(rt) == "") continue;
                if (!Visible(rt) || (rt.parent != null && rt.parent.name == "MenuButtons")) continue;
                // Move the HP/MP group as a unit; never move its bars independently.
                bool insideStatusGroup = false;
                for (Transform p = rt.parent; p != null; p = p.parent)
                    if (p.name == "hpmpTextPanel") { insideStatusGroup = true; break; }
                if (insideStatusGroup) continue;
                Selectable control = rt.GetComponent<Selectable>();
                if (control != null && !control.interactable) continue;
                Canvas canvas = rt.GetComponentInParent<Canvas>();
                if (canvas == null || canvas.renderMode == RenderMode.WorldSpace || rt.GetComponentInParent<RectMask2D>() != null || rt.GetComponentInParent<Mask>() != null) continue;
                Camera camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
                Rect bounds = Bounds(rt, camera);
                const float margin = 12f; // 2 pixels allowance for rounding/AA against the 10px acceptance limit.
                Rect v = ContentViewport();
                Rect safe = new Rect(v.x * Screen.width, v.y * Screen.height, v.width * Screen.width, v.height * Screen.height);
                if (bounds.width > safe.width - 2 * margin || bounds.height > safe.height - 2 * margin) continue;
                Vector2 shift = new Vector2(
                    bounds.xMin < safe.xMin + margin ? safe.xMin + margin - bounds.xMin : (bounds.xMax > safe.xMax - margin ? safe.xMax - margin - bounds.xMax : 0f),
                    bounds.yMin < safe.yMin + margin ? safe.yMin + margin - bounds.yMin : (bounds.yMax > safe.yMax - margin ? safe.yMax - margin - bounds.yMax : 0f));
                RectTransform parent = rt.parent as RectTransform;
                Vector2 p0, p1;
                if (shift != Vector2.zero && parent != null && RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, bounds.center, camera, out p0) &&
                    RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, bounds.center + shift, camera, out p1))
                {
                    if (!adjusted.ContainsKey(rt)) adjusted.Add(rt, rt.anchoredPosition);
                    rt.anchoredPosition += p1 - p0;
                }
            }
        }

        private static Rect Bounds(RectTransform rt, Camera camera)
        {
            if (Mathf.Abs(rt.rect.width) < 0.01f || Mathf.Abs(rt.rect.height) < 0.01f)
            {
                bool found = false;
                Rect combined = new Rect();
                foreach (Graphic graphic in rt.GetComponentsInChildren<Graphic>(false))
                {
                    RectTransform child = graphic.rectTransform;
                    if (child == rt || Mathf.Abs(child.rect.width) < 0.01f || Mathf.Abs(child.rect.height) < 0.01f || !Visible(child)) continue;
                    Rect b = Bounds(child, camera);
                    combined = found ? Rect.MinMaxRect(Mathf.Min(combined.xMin,b.xMin),Mathf.Min(combined.yMin,b.yMin),Mathf.Max(combined.xMax,b.xMax),Mathf.Max(combined.yMax,b.yMax)) : b;
                    found = true;
                }
                if (found) return combined;
            }
            Vector3[] corners = new Vector3[4];
            rt.GetWorldCorners(corners);
            Vector2 min = new Vector2(float.MaxValue, float.MaxValue), max = new Vector2(float.MinValue, float.MinValue);
            for (int i = 0; i < 4; i++)
            { Vector2 p = RectTransformUtility.WorldToScreenPoint(camera, corners[i]); min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }

        private IEnumerator ValidateSize(int width, int height, string mode = "current")
        {
            busy = true;
            bool wasBackground = Application.runInBackground;
            Application.runInBackground = true;
            frameCount = frameIndex = 0;
            privateStart = PrivateBytes();
            FullScreenMode expectedMode = mode == "windowed" ? FullScreenMode.Windowed :
                (mode == "fullscreen" ? FullScreenMode.FullScreenWindow : Screen.fullScreenMode);
            if (mode == "fullscreen") DataSet.SetDisplay(eDisplay.FullScreen);
            else if (width > 0 && expectedMode != FullScreenMode.Windowed) EnterFullscreen();
            else if (width > 0) Screen.SetResolution(width, height, expectedMode);
            yield return new WaitForSecondsRealtime(2f);
            transitionMaxMs = 0f;
            for (int i = 0; i < frameCount; i++) transitionMaxMs = Mathf.Max(transitionMaxMs, frameMs[i]);
            ApplyViewport();
            frameCount = frameIndex = 0;
            long memoryStart = GC.GetTotalMemory(false);
            yield return new WaitForSecondsRealtime(10f);
            yield return new WaitForEndOfFrame();
            try { ExportAudit(width, height, memoryStart, expectedMode); }
            catch (Exception e) { Logger.LogError("Audit failed: " + e); }
            finally { busy = false; Application.runInBackground = wasBackground; }
        }

        private void ExportAudit(int requestedWidth, int requestedHeight, long memoryStart, FullScreenMode expectedMode)
        {
            string stem = DateTime.Now.ToString("yyyyMMdd-HHmmss", CI) + "-" + Screen.width + "x" + Screen.height + "-" + Screen.fullScreenMode;
            string path = Path.Combine(reportDir, stem);
            var rows = new StringBuilder("path,kind,x,y,width,height,edgeMarginPx,fontPx,minFontPx,geometryResult\n");
            var buttons = new List<KeyValuePair<string, Rect>>();
            var categories = new HashSet<string>();
            int clipped = 0, smallText = 0;
            foreach (RectTransform rt in Resources.FindObjectsOfTypeAll<RectTransform>())
            {
                if (!rt.gameObject.scene.IsValid() || !rt.gameObject.activeInHierarchy) continue;
                Canvas canvas = rt.GetComponentInParent<Canvas>();
                if (canvas == null || canvas.renderMode == RenderMode.WorldSpace) continue;
                if (!Visible(rt)) continue;
                Camera camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
                Rect bounds = Bounds(rt, camera);
                string category = Category(rt);
                Text text = rt.GetComponent<Text>();
                TMPro.TMP_Text tmp = rt.GetComponent<TMPro.TMP_Text>();
                Button button = rt.GetComponent<Button>();
                if (category == "" && text == null && tmp == null && button == null) continue;
                string hierarchy = Hierarchy(rt);
                float margin = Mathf.Min(Mathf.Min(bounds.xMin, bounds.yMin), Mathf.Min(Screen.width - bounds.xMax, Screen.height - bounds.yMax));
                Rect v = ContentViewport();
                float contentMargin = Mathf.Min(Mathf.Min(bounds.xMin - v.x * Screen.width, bounds.yMin - v.y * Screen.height),
                    Mathf.Min(v.xMax * Screen.width - bounds.xMax, v.yMax * Screen.height - bounds.yMax));
                float font = text != null ? (text.resizeTextForBestFit ? text.cachedTextGenerator.fontSizeUsedForBestFit : text.fontSize) : (tmp != null ? tmp.fontSize : 0f);
                float localHeight = Mathf.Abs(rt.rect.height);
                float fontPx = localHeight > 0f ? font * bounds.height / localHeight : 0f;
                bool hasText = text != null ? !string.IsNullOrWhiteSpace(text.text) : (tmp != null && !string.IsNullOrWhiteSpace(tmp.text));
                // No design spec supplied: 12px baseline, 80% threshold = 9.6px.
                bool fontPass = !hasText || fontPx >= 9.6f;
                bool edgePass = category == "" ? margin >= 0f : (margin >= 10f && contentMargin >= 10f);
                if (category != "") categories.Add(category);
                if (!edgePass) clipped++;
                if (!fontPass) smallText++;
                rows.AppendFormat(CI, "{0},{1},{2:F2},{3:F2},{4:F2},{5:F2},{6:F2},{7:F2},9.6,{8}\n",
                    Csv(hierarchy), category == "" ? (button != null ? "Button" : "Text") : category,
                    bounds.x, bounds.y, bounds.width, bounds.height, margin, fontPx, edgePass && fontPass ? "PASS_GEOMETRY_ONLY" : "FAIL");
                if (button != null && bounds.width > 0 && bounds.height > 0 && rt.GetComponentInParent<Mask>() == null && rt.GetComponentInParent<RectMask2D>() == null)
                    buttons.Add(new KeyValuePair<string, Rect>(hierarchy, bounds));
            }
            File.WriteAllText(path + "-ui.csv", rows.ToString(), Encoding.UTF8);
            var overlaps = new StringBuilder("buttonA,buttonB,intersectionAreaPx2\n");
            int overlapCount = 0;
            for (int i = 0; i < buttons.Count; i++) for (int j = i + 1; j < buttons.Count; j++)
            {
                if (buttons[i].Key.StartsWith(buttons[j].Key + "/", StringComparison.Ordinal) || buttons[j].Key.StartsWith(buttons[i].Key + "/", StringComparison.Ordinal)) continue;
                Rect a = buttons[i].Value, b = buttons[j].Value;
                float area = Mathf.Max(0f, Mathf.Min(a.xMax,b.xMax)-Mathf.Max(a.xMin,b.xMin)) * Mathf.Max(0f,Mathf.Min(a.yMax,b.yMax)-Mathf.Max(a.yMin,b.yMin));
                if (area > 1f) { overlapCount++; overlaps.AppendFormat(CI,"{0},{1},{2:F2}\n",Csv(buttons[i].Key),Csv(buttons[j].Key),area); }
            }
            File.WriteAllText(path + "-overlaps.csv", overlaps.ToString(), Encoding.UTF8);
            float[] samples = new float[frameCount];
            Array.Copy(frameMs, samples, frameCount); Array.Sort(samples);
            float sum = 0f; foreach (float sample in samples) sum += sample;
            float p50 = samples.Length > 0 ? samples[samples.Length / 2] : 0f;
            float p99 = samples.Length > 0 ? samples[(int)((samples.Length - 1) * 0.99)] : 0f;
            Camera main = Camera.main;
            float ratioError = main == null ? -1f : Mathf.Abs(main.aspect / (main.pixelRect.width / main.pixelRect.height) - 1f) * 100f;
            string summary = string.Format(CI,
                "scene={0}\nrequested={1}x{2}\nactual={3}x{4}\nsizeMatched={5}\nframes={6}\nmeanFps={7:F2}\np50FrameMs={8:F3}\np99FrameMs={9:F3}\nmanagedGrowthBytes={10}\ncriticalEdgeFailures={11}\nsmallTextFailures={12}\nbuttonOverlapCandidates={13}\nBackPresent={14}\nPrimaryPresent={15}\nStatusPresent={16}\ncameraAspectErrorPercent={17:F6}\nverification=Geometry precheck only. Missing categories = NOT TESTED. Screenshot review required for clipping, localization, image aspect, occlusion, and all non-button overlaps.\n",
                SceneManager.GetActiveScene().name,requestedWidth,requestedHeight,Screen.width,Screen.height,
                requestedWidth == 0 || (requestedWidth == Screen.width && requestedHeight == Screen.height),samples.Length,
                sum > 0 ? samples.Length * 1000f / sum : 0,p50,p99,GC.GetTotalMemory(false)-memoryStart,clipped,smallText,overlapCount,
                categories.Contains("Back"),categories.Contains("Primary"),categories.Contains("Status"),ratioError);
            File.WriteAllText(path + "-summary.txt", summary, Encoding.UTF8);
            File.AppendAllText(path + "-summary.txt", string.Format(CI,
                "transitionMaxFrameMs={0:F3}\nprivateGrowthBytes={1}\nmeasurement=10s steady-state after 2s settling; transitionMax covers the first 2s; memory includes game and driver allocations, before screenshot.\n",
                transitionMaxMs,PrivateBytes() - privateStart), Encoding.UTF8);
            File.AppendAllText(path + "-summary.txt", "contentViewport=" + ContentViewport() + "\nexpandWorld=" + expandWorld.Value + "\n", Encoding.UTF8);
            Vector2Int desktop = NativeDesktop();
            File.AppendAllText(path + "-summary.txt", string.Format(CI,
                "pluginVersion=1.0.1\nfullscreenMode={0}\nrequestedMode={1}\nmodeMatched={2}\nnativeDesktop={3}x{4}\nnativeSizeMatched={5}\ndisplayRendering={6}x{7}\ndisplaySystem={8}x{9}\n",
                Screen.fullScreenMode, expectedMode, Screen.fullScreenMode == expectedMode, desktop.x, desktop.y,
                Screen.width == desktop.x && Screen.height == desktop.y,
                Display.main.renderingWidth, Display.main.renderingHeight, Display.main.systemWidth, Display.main.systemHeight), Encoding.UTF8);
            var cameras = new StringBuilder("name,enabled,rect,pixelRect,aspect,targetTexture\n");
            foreach (Camera camera in Resources.FindObjectsOfTypeAll<Camera>())
                if (camera.gameObject.scene.IsValid()) cameras.AppendFormat(CI, "{0},{1},{2},{3},{4:F6},{5}\n",
                    Csv(Hierarchy(camera.transform)), camera.enabled, Csv(camera.rect.ToString()), Csv(camera.pixelRect.ToString()), camera.aspect,
                    Csv(camera.targetTexture == null ? "direct" : camera.targetTexture.width + "x" + camera.targetTexture.height));
            File.WriteAllText(path + "-cameras.csv", cameras.ToString(), Encoding.UTF8);
            ScreenCapture.CaptureScreenshot(path + ".png");
            Logger.LogInfo("Audit exported: " + path + "\n" + summary);
        }

        private static bool Visible(RectTransform rt)
        {
            Graphic graphic = rt.GetComponent<Graphic>();
            if (graphic != null && (!graphic.enabled || graphic.color.a < 0.01f)) return false;
            Transform current = rt;
            while (current != null)
            {
                CanvasGroup group = current.GetComponent<CanvasGroup>();
                if (group != null && group.alpha < 0.01f) return false;
                if (group != null && group.ignoreParentGroups) break;
                current = current.parent;
            }
            return true;
        }
        private static string Hierarchy(Transform t)
        { string s = t.name; while (t.parent != null) { t = t.parent; s = t.name + "/" + s; } return s; }
        private static string Csv(string s) { return "\"" + s.Replace("\"", "\"\"") + "\""; }
        private static long PrivateBytes()
        { using (var process = System.Diagnostics.Process.GetCurrentProcess()) return process.PrivateMemorySize64; }

        private void RestoreOutput()
        {
            if (directCamera != null) { directCamera.targetTexture = originalTexture; directCamera.rect = originalRect; directCamera.ResetAspect(); }
            if (outputImage != null) outputImage.enabled = originalImageEnabled;
            if (outputCamera != null) outputCamera.enabled = originalOutputEnabled;
            directCamera = null; outputImage = null; outputCamera = null;
        }
        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= SceneLoaded;
            if (harmony != null) harmony.UnpatchSelf();
            RestoreOutput();
            foreach (var pair in uiCameraRects) if (pair.Key != null) { pair.Key.rect = pair.Value; pair.Key.ResetAspect(); }
            if (borderCamera != null) Destroy(borderCamera.gameObject);
            foreach (var pair in adjusted) if (pair.Key != null) pair.Key.anchoredPosition = pair.Value;
            adjusted.Clear();
            if (Instance == this) Instance = null;
        }
    }

    [HarmonyPatch(typeof(UIScreen), "SetCameraAndRenderTextureIndex")]
    internal static class ScreenPatch
    { private static void Postfix(UIScreen __instance) { if (ResolutionPlugin.Instance != null) ResolutionPlugin.Instance.UseDirectCamera(__instance); } }

    [HarmonyPatch(typeof(CameraSwitchHelper), "WorldToScreenPoint")]
    internal static class ProjectionPatch
    {
        private static bool Prefix(Camera camera, Vector3 position, ref Vector3 __result)
        {
            if (ResolutionPlugin.Instance == null || !ResolutionPlugin.Instance.IsDirect(camera)) return true;
            __result = camera.WorldToScreenPoint(position); return false;
        }
    }
    [HarmonyPatch(typeof(CameraSwitchHelper), "ScreenPointToRay")]
    internal static class RayPatch
    {
        private static bool Prefix(Camera camera, Vector2 position, ref Ray __result)
        {
            if (ResolutionPlugin.Instance == null || !ResolutionPlugin.Instance.IsDirect(camera)) return true;
            __result = camera.ScreenPointToRay(position); return false;
        }
    }
    [HarmonyPatch(typeof(DataSet), "SetDisplay")]
    internal static class DisplayPatch
    { private static bool Prefix(eDisplay dis) { return ResolutionPlugin.Instance == null || ResolutionPlugin.Instance.ChangeDisplay(dis); } }

    [HarmonyPatch(typeof(DataSet), "Awake")]
    internal static class DataStartupPatch
    {
        private static void Prefix(out Vector3Int __state)
        { __state = new Vector3Int(Screen.width, Screen.height, (int)Screen.fullScreenMode); }
        private static void Postfix(Vector3Int __state)
        { if (ResolutionPlugin.Instance != null) ResolutionPlugin.Instance.AfterDataAwake(__state); }
    }

    [HarmonyPatch(typeof(DataSet), "SetResolution", new Type[] { typeof(eResolution) })]
    internal static class ResolutionChoicePatch
    { private static bool Prefix(eResolution res) { return ResolutionPlugin.Instance == null || ResolutionPlugin.Instance.ChangeResolution(res); } }

    [HarmonyPatch(typeof(DataSet), "SetResolution", new Type[] { typeof(Resolution), typeof(FullScreenMode) })]
    internal static class ResolutionModePatch
    { private static bool Prefix(Resolution res, FullScreenMode mode) { return ResolutionPlugin.Instance == null || ResolutionPlugin.Instance.ChangeResolution(res, mode); } }
}
