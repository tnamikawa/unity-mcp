using System;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Services
{
    public class CodeExecutionGateTests
    {
        [TestCase(true, false, false, "compiling")]
        [TestCase(false, true, false, "asset_update")]
        [TestCase(false, false, true, "playmode_transition")]
        public void BusyEditorDoesNotStartExecution(bool compiling, bool updating, bool changing, string expected)
        {
            var gate = new CodeExecutionGate();
            Assert.IsFalse(gate.TryBegin(compiling, updating, changing, out var execution, out var reason));
            Assert.IsNull(execution);
            Assert.AreEqual(expected, reason);
            Assert.IsNull(gate.Active);
        }

        [Test]
        public void RequestBlocksBeforeCompilationStartsAndUntilReloadCompletes()
        {
            var gate = new CodeExecutionGate();
            gate.CompilationRequested();
            AssertBlocked(gate, "compilation_pending");
            gate.CompilationStarted();
            gate.CompilationFinished(false);
            AssertBlocked(gate, "compilation_pending");
            gate.ReloadCompleted(gate.Generation);
            Assert.IsTrue(gate.TryBegin(false, false, false, out var execution, out _));
            execution.Dispose();
        }

        [Test]
        public void OldReloadCannotReleaseNewCompilationRequest()
        {
            var gate = new CodeExecutionGate();
            gate.CompilationRequested();
            int oldGeneration = gate.Generation;
            gate.CompilationRequested();
            gate.ReloadCompleted(oldGeneration);
            AssertBlocked(gate, "compilation_pending");
        }

        [Test]
        public void PendingCompilationSurvivesDomainStateReconstruction()
        {
            var before = new CodeExecutionGate();
            before.CompilationRequested();
            before.CompilationStarted();
            before.CompilationFinished(false);
            var after = new CodeExecutionGate(before.CompilationPending, before.CompilationFailed, before.Generation);
            AssertBlocked(after, "compilation_pending");
            after.ReloadCompleted(after.Generation);
            Assert.IsNull(after.ReadinessReason(false, false, false));
        }

        [Test]
        public void FailedCompilationBlocksStaleAssembliesUntilSuccessfulCompileAndReload()
        {
            var gate = new CodeExecutionGate();
            gate.CompilationStarted();
            gate.CompilationFinished(true);
            AssertBlocked(gate, "compilation_failed");
            gate.CompilationStarted();
            gate.CompilationFinished(false);
            AssertBlocked(gate, "compilation_pending");
            gate.ReloadCompleted(gate.Generation);
            Assert.IsNull(gate.ReadinessReason(false, false, false));
        }

        [Test]
        public async Task CancelledResponseWaitDoesNotReleaseRunningCode()
        {
            var gate = new CodeExecutionGate();
            Assert.IsTrue(gate.TryBegin(false, false, false, out var execution, out _));
            var response = new TaskCompletionSource<bool>();
            using (var cancellation = new CancellationTokenSource())
            using (cancellation.Token.Register(() => response.TrySetCanceled()))
            {
                cancellation.Cancel();
                Assert.IsTrue(response.Task.IsCanceled);
                var active = await Task.Run(() => gate.Active);
                Assert.AreSame(execution, active);
                AssertBlocked(gate, "execution_in_progress");
            }
            execution.Dispose();
            Assert.IsTrue(gate.TryBegin(false, false, false, out var next, out _));
            execution.Dispose(); // Late cleanup from the old call cannot release the next one.
            Assert.AreSame(next, gate.Active);
            next.Dispose();
        }

        [Test]
        public void ExceptionCleanupAllowsNextExecution()
        {
            var gate = new CodeExecutionGate();
            Assert.Throws<InvalidOperationException>(() =>
            {
                Assert.IsTrue(gate.TryBegin(false, false, false, out var execution, out _));
                using (execution) throw new InvalidOperationException();
            });
            Assert.IsNull(gate.Active);
        }

        private static void AssertBlocked(CodeExecutionGate gate, string expected)
        {
            Assert.IsFalse(gate.TryBegin(false, false, false, out var execution, out var reason));
            Assert.IsNull(execution);
            Assert.AreEqual(expected, reason);
        }
    }
}
