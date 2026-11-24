using System;
using System.Linq;
using System.Threading;
using MCPForUnity.Editor.Helpers;
using UnityEditor;
using UnityEditor.Compilation;

namespace MCPForUnity.Editor.Services
{
    [InitializeOnLoad]
    internal static class EditorExecutionGuard
    {
        private const string PendingKey = "MCPForUnity.Execution.CompilationPending";
        private const string FailedKey = "MCPForUnity.Execution.CompilationFailed";
        private const string GenerationKey = "MCPForUnity.Execution.CompilationGeneration";
        private static readonly int MainThreadId = Thread.CurrentThread.ManagedThreadId;
        private static readonly CodeExecutionGate Gate = new CodeExecutionGate(
            SessionState.GetBool(PendingKey, false), SessionState.GetBool(FailedKey, false),
            SessionState.GetInt(GenerationKey, 0));
        // Capture the generation loaded into this domain before other reload callbacks
        // can request another compilation. That newer request must keep its own gate.
        private static readonly int LoadedGeneration = Gate.Generation;
        private static bool _compileHadErrors;
        private static int _editorAssembliesCompiled;
        private static bool _changingPlayMode;
        private static volatile bool _buildingPlayer;
        private static long _buildStartedUnixMs;

        internal static bool CompilationPending => Gate.CompilationPending;
        // Readable from transport threads while a player build holds the main thread.
        internal static bool IsBuildingPlayer => _buildingPlayer;
        internal static bool CompilationFailed => Gate.CompilationFailed;
        internal static bool IsChangingPlayMode => _changingPlayMode ||
            EditorApplication.isPlaying != EditorApplication.isPlayingOrWillChangePlaymode;

        static EditorExecutionGuard()
        {
            CompilationPipeline.compilationStarted += _ =>
            {
                _compileHadErrors = false;
                _editorAssembliesCompiled = 0;
                Gate.CompilationStarted();
                SaveCompilationState();
            };
            CompilationPipeline.assemblyCompilationFinished += (path, messages) =>
            {
                _compileHadErrors |= messages.Any(m => m.type == CompilerMessageType.Error);
                if (IsEditorAssembly(path)) _editorAssembliesCompiled++;
            };
            CompilationPipeline.compilationFinished += _ =>
            {
                // Only a rebuilt Editor assembly is followed by a domain reload.
                Gate.CompilationFinished(_compileHadErrors, _editorAssembliesCompiled > 0);
                SaveCompilationState();
            };
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                Gate.CompilationRequested();
                SaveCompilationState();
            };
            AssemblyReloadEvents.afterAssemblyReload += () =>
            {
                // Assemblies are loaded at this notification. Inspector updates can be
                // deferred independently, so they are not part of compilation readiness.
                Gate.ReloadCompleted(LoadedGeneration);
                SaveCompilationState();
            };
            EditorApplication.playModeStateChanged += state =>
                _changingPlayMode = state == PlayModeStateChange.ExitingEditMode
                    || state == PlayModeStateChange.ExitingPlayMode;
            // Post-process callbacks are not a reliable end signal (a failed build skips them);
            // the main thread ticking again with no build running is.
            EditorApplication.update += () =>
            {
                if (_buildingPlayer && !BuildPipeline.isBuildingPlayer) _buildingPlayer = false;
            };
        }

        internal static void PlayerBuildStarted()
        {
            _buildStartedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _buildingPlayer = true;
        }

        internal static string BuildingPlayerState() => Newtonsoft.Json.JsonConvert.SerializeObject(new
        {
            status = "success",
            result = new
            {
                success = true,
                data = new
                {
                    observed_at_unix_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    build = new { is_building_player = true, started_at_unix_ms = _buildStartedUnixMs },
                    execution = new { is_running = Gate.Active != null }
                }
            }
        });

        // Editor assemblies are written to Library/ScriptAssemblies. Player builds compile into
        // other folders (PlayerScriptAssemblies, Bee artifacts) and never reload the Editor domain.
        internal static bool IsEditorAssembly(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string normalized = path.Replace('\\', '/');
            return normalized.IndexOf("/PlayerScriptAssemblies/", StringComparison.OrdinalIgnoreCase) < 0 &&
                (normalized.StartsWith("Library/ScriptAssemblies/", StringComparison.OrdinalIgnoreCase) ||
                 normalized.IndexOf("/Library/ScriptAssemblies/", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        internal static void RequestScriptCompilation()
        {
            // Mark before queuing: isCompiling may still be false until the next Editor tick.
            bool wasPending = Gate.CompilationPending;
            Gate.CompilationRequested();
            int generation = Gate.Generation;
            SaveCompilationState();
            try { CompilationPipeline.RequestScriptCompilation(); }
            catch
            {
                Gate.CompilationRequestFailed(generation, wasPending);
                SaveCompilationState();
                throw;
            }
        }

        private static void SaveCompilationState()
        {
            SessionState.SetBool(PendingKey, Gate.CompilationPending);
            SessionState.SetBool(FailedKey, Gate.CompilationFailed);
            SessionState.SetInt(GenerationKey, Gate.Generation);
        }

        internal static ErrorResponse CheckReadiness()
        {
            if (Thread.CurrentThread.ManagedThreadId != MainThreadId)
                return Busy("not_main_thread");
            string reason = Gate.ReadinessReason(EditorApplication.isCompiling,
                EditorApplication.isUpdating, IsChangingPlayMode);
            return reason == null ? null : Busy(reason);
        }

        internal static bool TryBegin(out CodeExecutionGate.Execution execution, out ErrorResponse error)
        {
            execution = null;
            error = CheckReadiness();
            if (error != null) return false;
            if (Gate.TryBegin(EditorApplication.isCompiling, EditorApplication.isUpdating,
                IsChangingPlayMode, out execution, out string reason)) return true;
            error = Busy(reason);
            return false;
        }

        // Safe on a transport thread; no Unity API calls. Cancellation of a response wait
        // never disposes this lease: only returning from the C# handler does.
        internal static ErrorResponse GetActiveExecutionError()
        {
            var active = Gate.Active;
            return active == null ? null : new ErrorResponse("execution_in_progress", new
            {
                reason = "execution_in_progress",
                execution_id = active.Id,
                started_at_unix_ms = active.StartedUnixMs,
                retry_after_ms = 500,
                execution_stopped = false
            });
        }

        private static ErrorResponse Busy(string reason) => new ErrorResponse("editor_busy", new
        {
            reason,
            retry_after_ms = 500,
            execution_started = false
        });
    }

    internal sealed class PlayerBuildExecutionGuard : UnityEditor.Build.IPreprocessBuildWithReport
    {
        public int callbackOrder => int.MinValue;

        public void OnPreprocessBuild(UnityEditor.Build.Reporting.BuildReport report) =>
            EditorExecutionGuard.PlayerBuildStarted();
    }

    // SessionState survives assembly reloads and is cleared when the Editor exits, which is
    // exactly the lifetime of the execution ledger. Main thread only.
    internal sealed class SessionStateJournalStore : IExecutionJournalStore
    {
        public string Read(string key)
        {
            string value = SessionState.GetString(key, null);
            return string.IsNullOrEmpty(value) ? null : value;
        }

        public void Write(string key, string value) => SessionState.SetString(key, value);

        public void Erase(string key) => SessionState.EraseString(key);
    }
}
