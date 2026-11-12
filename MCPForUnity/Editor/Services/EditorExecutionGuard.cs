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
        private static bool _compileHadErrors;
        private static bool _changingPlayMode;

        internal static bool CompilationPending => Gate.CompilationPending;
        internal static bool CompilationFailed => Gate.CompilationFailed;
        internal static bool IsChangingPlayMode => _changingPlayMode ||
            EditorApplication.isPlaying != EditorApplication.isPlayingOrWillChangePlaymode;

        static EditorExecutionGuard()
        {
            CompilationPipeline.compilationStarted += _ =>
            {
                _compileHadErrors = false;
                Gate.CompilationStarted();
                SaveCompilationState();
            };
            CompilationPipeline.assemblyCompilationFinished += (_, messages) =>
                _compileHadErrors |= messages.Any(m => m.type == CompilerMessageType.Error);
            CompilationPipeline.compilationFinished += _ =>
            {
                Gate.CompilationFinished(_compileHadErrors);
                SaveCompilationState();
            };
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                Gate.CompilationRequested();
                SaveCompilationState();
            };
            AssemblyReloadEvents.afterAssemblyReload += () =>
            {
                int generation = Gate.Generation;
                // Let all reload callbacks finish before allowing code against the new assemblies.
                EditorApplication.delayCall += () =>
                {
                    Gate.ReloadCompleted(generation);
                    SaveCompilationState();
                };
            };
            EditorApplication.playModeStateChanged += state =>
                _changingPlayMode = state == PlayModeStateChange.ExitingEditMode
                    || state == PlayModeStateChange.ExitingPlayMode;
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
                if (!wasPending) Gate.ReloadCompleted(generation);
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
}
