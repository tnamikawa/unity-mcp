using System;

namespace MCPForUnity.Editor.Services
{
    // Kept independent of Unity so lifecycle races can be tested without running Editor code.
    internal sealed class CodeExecutionGate
    {
        private readonly object _lock = new object();
        private Execution _active;
        private bool _compilationPending;
        private bool _compilationFailed;
        private int _generation;

        internal CodeExecutionGate(bool pending = false, bool failed = false, int generation = 0)
        {
            _compilationPending = pending;
            _compilationFailed = failed;
            _generation = generation;
        }

        internal bool CompilationPending { get { lock (_lock) return _compilationPending; } }
        internal bool CompilationFailed { get { lock (_lock) return _compilationFailed; } }
        internal int Generation { get { lock (_lock) return _generation; } }
        internal Execution Active { get { lock (_lock) return _active; } }

        internal void CompilationRequested()
        {
            lock (_lock)
            {
                _generation++;
                _compilationPending = true;
            }
        }

        internal void CompilationStarted()
        {
            lock (_lock)
            {
                _generation++;
                _compilationPending = true;
                _compilationFailed = false;
            }
        }

        internal void CompilationRequestFailed(int generation, bool wasPending)
        {
            lock (_lock)
            {
                // Restore the previous request when Unity rejects this call. A newer
                // request or started compilation owns its own generation and stays intact.
                if (_generation != generation) return;
                _generation--;
                _compilationPending = wasPending;
            }
        }

        internal void CompilationFinished(bool failed)
        {
            lock (_lock)
            {
                // Successful compilation still has to swap assemblies. A failed one will not reload.
                _compilationPending = !failed;
                _compilationFailed = failed;
            }
        }

        internal void ReloadCompleted(int generation)
        {
            lock (_lock)
            {
                // An old reload callback must not clear a newer compilation request.
                if (_generation == generation) _compilationPending = false;
            }
        }

        internal string ReadinessReason(bool compiling, bool updating, bool changingPlayMode)
        {
            lock (_lock) return ReadinessReasonLocked(compiling, updating, changingPlayMode);
        }

        private string ReadinessReasonLocked(bool compiling, bool updating, bool changingPlayMode)
        {
            if (compiling) return "compiling";
            if (_compilationPending) return "compilation_pending";
            if (_compilationFailed) return "compilation_failed";
            if (updating) return "asset_update";
            if (changingPlayMode) return "playmode_transition";
            return null;
        }

        internal bool TryBegin(bool compiling, bool updating, bool changingPlayMode,
            out Execution execution, out string reason)
        {
            lock (_lock)
            {
                execution = null;
                reason = _active != null ? "execution_in_progress"
                    : ReadinessReasonLocked(compiling, updating, changingPlayMode);
                if (reason != null) return false;
                execution = _active = new Execution(this);
                return true;
            }
        }

        internal sealed class Execution : IDisposable
        {
            private readonly CodeExecutionGate _owner;
            internal string Id { get; } = Guid.NewGuid().ToString("N");
            internal long StartedUnixMs { get; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            internal Execution(CodeExecutionGate owner) { _owner = owner; }

            public void Dispose()
            {
                lock (_owner._lock)
                {
                    if (ReferenceEquals(_owner._active, this)) _owner._active = null;
                }
            }
        }
    }
}
