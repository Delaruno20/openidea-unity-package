using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OpenIDEA.Unity
{
    /// Play mode, compilation and refresh: what Rider's Unity toolbar shows and drives.
    internal static class EditorState
    {
        static string lastSnapshot;
        static double lastCheck;

        /// The latest state, safe to read from any thread (null until the first editor update).
        public static string Last => lastSnapshot;

        public static void Start()
        {
            EditorApplication.playModeStateChanged += _ => Notify();
            EditorApplication.pauseStateChanged += _ => Notify();
            CompilationPipeline.compilationStarted += _ => Notify();
            CompilationPipeline.compilationFinished += _ => Notify();
        }

        /// Catches changes without an event (an import starting or ending).
        public static void Pump()
        {
            var time = EditorApplication.timeSinceStartup;
            if (lastSnapshot != null && time - lastCheck < 0.25) return;
            lastCheck = time;
            var now = Snapshot("state");
            if (now != lastSnapshot) Notify(now);
        }

        static void Notify(string snapshot = null)
        {
            lastSnapshot = snapshot ?? Snapshot("state");
            BridgeServer.Broadcast(lastSnapshot);
        }

        public static string Snapshot(string type)
        {
            return new Json().Begin()
                .Field("type", type)
                .Field("unityVersion", Application.unityVersion)
                .Field("projectName", Application.productName)
                .Field("playing", EditorApplication.isPlaying)
                .Field("willPlay", EditorApplication.isPlayingOrWillChangePlaymode)
                .Field("paused", EditorApplication.isPaused)
                .Field("compiling", EditorApplication.isCompiling)
                .Field("updating", EditorApplication.isUpdating)
                .Field("refreshOnSave", OpenIdeaSettings.RefreshOnSave)
                .Field("autoRefresh", EditorPrefs.GetInt("kAutoRefreshMode", EditorPrefs.GetBool("kAutoRefresh", true) ? 1 : 0) != 0)
                .Field("testsRunning", TestBridge.Running)
                .End().ToString();
        }

        /// `on`, `off` or anything else to toggle.
        public static string Play(string state)
        {
            var playing = EditorApplication.isPlayingOrWillChangePlaymode;
            var want = state == "on" ? true : state == "off" ? false : !playing;
            if (want != playing) EditorApplication.isPlaying = want;
            return Snapshot("state");
        }

        public static string Pause(string state)
        {
            var paused = EditorApplication.isPaused;
            var want = state == "on" ? true : state == "off" ? false : !paused;
            if (want != paused) EditorApplication.isPaused = want;
            return Snapshot("state");
        }

        public static string Step()
        {
            if (!EditorApplication.isPlaying) EditorApplication.isPaused = true;
            EditorApplication.Step();
            return Snapshot("state");
        }

        /// `auto`: a refresh because a file was saved; skipped while playing so play mode isn't
        /// interrupted by a recompile (Unity refreshes when play stops).
        public static string Refresh(bool force, bool auto)
        {
            if (auto && (EditorApplication.isPlayingOrWillChangePlaymode || !OpenIdeaSettings.RefreshOnSave))
                return new Json().Begin().Field("refreshed", false).End().ToString();
            AssetDatabase.Refresh(force ? ImportAssetOptions.ForceUpdate : ImportAssetOptions.Default);
            return new Json().Begin().Field("refreshed", true).End().ToString();
        }

        public static string Menu(string path)
        {
            var ok = !string.IsNullOrEmpty(path) && EditorApplication.ExecuteMenuItem(path);
            return new Json().Begin().Field("ok", ok).End().ToString();
        }

        /// Selects and pings an asset, or the GameObject behind a component of an open scene
        /// (`fileId` is the component's local id in the scene file): Rider's "Show in Unity".
        public static string Select(string path, string fileId)
        {
            var asset = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadMainAssetAtPath(path);
            if (asset == null)
                return new Json().Begin().Field("ok", false).End().ToString();

            Object target = asset;
            var inScene = false;
            if (path.EndsWith(".unity") && ulong.TryParse(fileId, out var id) && SceneManager.GetSceneByPath(path).isLoaded)
            {
                var guid = AssetDatabase.AssetPathToGUID(path);
                if (GlobalObjectId.TryParse($"GlobalObjectId_V1-2-{guid}-{id}-0", out var globalId))
                {
                    var found = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(globalId);
                    if (found != null)
                    {
                        target = found is Component c ? c.gameObject : found;
                        inScene = true;
                    }
                }
            }

            Selection.activeObject = target;
            EditorGUIUtility.PingObject(target);
            if (inScene && SceneView.lastActiveSceneView != null)
                SceneView.lastActiveSceneView.FrameSelected();
            EditorApplication.ExecuteMenuItem(inScene ? "Window/General/Hierarchy" : "Window/General/Project");
            return new Json().Begin().Field("ok", true).Field("inScene", inScene).End().ToString();
        }
    }
}
