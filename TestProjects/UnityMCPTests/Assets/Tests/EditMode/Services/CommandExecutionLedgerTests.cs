using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Services
{
    public class CommandExecutionLedgerTests
    {
        // Stands in for SessionState: survives a new ledger (domain reload), cleared by Restart().
        private sealed class MemoryStore : IExecutionJournalStore
        {
            public readonly Dictionary<string, string> Values = new();
            public int Writes;
            public bool Fail;
            public string Read(string key) => Values.TryGetValue(key, out var value) ? value : null;
            public void Write(string key, string value)
            {
                if (Fail) throw new System.IO.IOException("sharing violation");
                Writes++;
                Values[key] = value;
            }
            public void Erase(string key) { if (Fail) throw new System.IO.IOException("sharing violation"); Values.Remove(key); }
        }

        private MemoryStore store;
        private long now;
        private long processStarted;
        private CommandExecutionLedger ledger;
        private const string Success = "{\"status\":\"success\",\"result\":{\"success\":true,\"data\":{\"value\":42}}}";
        private const string Busy = "{\"status\":\"success\",\"result\":{\"success\":false,\"error\":\"editor_busy\"}}";

        [SetUp]
        public void SetUp()
        {
            store = new MemoryStore();
            now = 100000;
            processStarted = 1000;
            ledger = New();
        }

        private CommandExecutionLedger New() => new CommandExecutionLedger(store, "process-A", processStarted, () => now);

        private void Reload() { ledger.Flush(); ledger = New(); }

        private void Restart(long startedAt)
        {
            store = new MemoryStore();
            processStarted = startedAt;
            ledger = New();
        }

        private static JObject Request(string id = "first", string code = "return 42;", int? attempt = null)
        {
            var request = new JObject { ["action"] = "execute", ["code"] = code, ["execution_id"] = id };
            if (attempt != null) request["attempt"] = attempt.Value;
            return request;
        }
        private static JObject Result(string response) => (JObject)JObject.Parse(response)["result"];
        private static string State(string response) => Result(response)["data"]["execution"].Value<string>("state");
        private string Admit(string id = "first", string code = "return 42;", int? attempt = null) =>
            ledger.Admit("execute_code", Request(id, code, attempt), out _, out _);

        [Test]
        public void SettlingMissingIdPreventsDelayedAdmissionAcrossReload()
        {
            Assert.AreEqual("not_started", State(ledger.Status("first", true)));
            Assert.IsNotNull(Admit());
            Reload();
            Assert.IsNotNull(Admit());
            Assert.IsNull(Admit("next"));
        }

        [Test]
        public void SettlingQueuedIdPreventsLateStartAndReleasesSlot()
        {
            Admit();
            Assert.AreEqual("not_started", State(ledger.Status("first", true)));
            Assert.IsFalse(ledger.Start("first", 0));
            Assert.IsNull(Admit("next"));
        }

        [Test]
        public void SettlingRunningIdPreservesActiveExecution()
        {
            Admit(); ledger.Start("first", 0);
            Assert.AreEqual("running", State(ledger.Status("first", true)));
            Assert.IsNotNull(Admit("next"));
            ledger.Complete("first", 0, Success);
            Assert.IsNull(Admit("next"));
        }

        [Test]
        public void QueuedWorkDiscardedByReloadIsUnstarted()
        {
            Admit();
            Reload();
            Assert.AreEqual("not_started", State(ledger.Status("first")));
            Assert.IsFalse(ledger.Start("first", 0));
            Assert.IsNull(Admit("next"));
        }

        [Test]
        public void QueuedWorkNeverFlushedIsUnknownAfterReloadAndSettlesUnstarted()
        {
            // Admission happens on a transport thread and is not written; the domain that
            // held the queued command is gone, so it can never start.
            Admit();
            ledger = New();
            Assert.AreEqual("not_started", State(ledger.Status("first", true, 0, now)));
            Assert.IsNull(Admit(attempt: 1));
        }

        [Test]
        public void StartPersistsRunningBeforeTheHandler()
        {
            Admit();
            Assert.IsTrue(ledger.Start("first", 0));
            ledger = New();
            Assert.AreEqual("interrupted", State(ledger.Status("first", true)));
            Assert.IsNotNull(Admit(attempt: 5));
        }

        [Test]
        public void StoreFailureRefusesStartAndRecoversOnNextFlush()
        {
            Admit();
            store.Fail = true;
            Assert.IsFalse(ledger.Start("first", 0));
            Assert.AreEqual("not_started", Result(ledger.CancelWait("first", 0))["data"].Value<string>("state"));
            Assert.IsNotNull(Result(ledger.Status())["data"].Value<string>("journal_error"));
            store.Fail = false;
            Assert.IsTrue(ledger.Flush());
            Assert.IsNull(Result(ledger.Status())["data"].Value<string>("journal_error"));
            Assert.IsNull(Admit(attempt: 1));
            Assert.IsTrue(ledger.Start("first", 1));
        }

        [Test]
        public void BusyBeforeStartAdmitsTheNextAttemptOnly()
        {
            Admit(attempt: 0);
            ledger.Complete("first", 0, Busy);
            Assert.AreEqual("execution_not_started", Result(Admit(attempt: 0)).Value<string>("error"));
            Assert.IsNull(Admit(attempt: 1));
            Assert.IsFalse(ledger.Start("first", 0));
            Assert.IsTrue(ledger.Start("first", 1));
        }

        [Test]
        public void LegacyBusyBeforeStartDefersWithoutReservingAnExecutedId()
        {
            Admit();
            ledger.Complete("first", 0, Busy);
            Assert.IsNull(Admit());
            Assert.IsTrue(ledger.Start("first", 0));
        }

        [Test]
        public void SettledAttemptRejectsDelayedCopyAndAdmitsResend()
        {
            Assert.AreEqual("not_started", State(ledger.Status("first", true, 0, now)));
            Assert.AreEqual("execution_not_started", Result(Admit(attempt: 0)).Value<string>("error"));
            Assert.IsNull(Admit(attempt: 1));
            Assert.IsTrue(ledger.Start("first", 1));
            string response = ledger.Complete("first", 1, Success);
            Assert.AreEqual(response, Admit(attempt: 2));
        }

        [Test]
        public void ResendAfterSettleDoesNotLetTheOldQueuedCopyStart()
        {
            Admit(attempt: 0);
            ledger.Status("first", true, 0, now);
            Assert.IsNull(Admit(attempt: 1));
            Assert.IsFalse(ledger.Start("first", 0));
            Assert.AreEqual("execution_not_started", Result(ledger.CancelWait("first", 0)).Value<string>("error"));
            Assert.AreEqual("queued", State(ledger.Status("first")));
            Assert.IsTrue(ledger.Start("first", 1));
            Assert.AreEqual(42, Result(ledger.Complete("first", 1, Success))["data"].Value<int>("value"));
            Assert.AreEqual(Success.Length, ledger.Complete("first", 0, Success).Length);
            Assert.AreEqual("completed", State(ledger.Status("first")));
        }

        [Test]
        public void FinalSettleNeverAdmitsAgain()
        {
            ledger.Status("first", true);
            Assert.AreEqual("execution_not_started", Result(Admit(attempt: 1000)).Value<string>("error"));
        }

        [Test]
        public void UnknownIdAfterEditorRestartIsLostNotUnstarted()
        {
            long admitted = now;
            Restart(now + 60000);
            Assert.AreEqual("lost", State(ledger.Status("first", true, 0, admitted)));
            Assert.AreEqual("execution_lost", Result(Admit(attempt: 3)).Value<string>("error"));
        }

        [Test]
        public void UnknownIdAdmittedAfterProcessStartIsUnstarted()
        {
            Assert.AreEqual("not_started", State(ledger.Status("first", true, 0, processStarted + 60000)));
        }

        [Test]
        public void CorruptStoreReportsUnknownIdsAsLost()
        {
            store.Values[CommandExecutionLedger.DocumentKey] = "broken json";
            ledger = New();
            Assert.IsTrue(Result(ledger.Status())["data"].Value<bool>("history_incomplete"));
            Assert.AreEqual("lost", State(ledger.Status("first", true, 0, now)));
            Assert.IsNull(Admit("next"));
        }

        [Test]
        public void ConcurrentClientsAdmitOnlyOneCodeExecution()
        {
            var responses = new string[12];
            Parallel.For(0, responses.Length, i => responses[i] = Admit("request-" + i));
            Assert.AreEqual(1, responses.Count(r => r == null));
            Assert.AreEqual(11, responses.Count(r => r != null && Result(r).Value<string>("error") == "execution_in_progress"));
        }

        [Test]
        public void SameIdReturnsCompletedResultWithoutAdmission()
        {
            Assert.IsNull(Admit());
            Assert.IsTrue(ledger.Start("first", 0));
            string response = ledger.Complete("first", 0, Success);
            Assert.AreEqual(response, Admit());
            Assert.AreEqual(42, Result(response)["data"].Value<int>("value"));
            Assert.IsTrue(Result(response)["execution"].Value<bool>("result_retained"));
        }

        [Test]
        public void CompletedResultSurvivesReload()
        {
            Admit(); ledger.Start("first", 0);
            string response = ledger.Complete("first", 0, Success);
            Reload();
            Assert.AreEqual("completed", State(ledger.Status("first", true)));
            Assert.AreEqual(response, Admit());
        }

        [Test]
        public void ChangedArgumentsCannotReuseAnId()
        {
            Assert.IsNull(Admit());
            Assert.AreEqual("execution_id_conflict", Result(Admit(code: "return 99;")).Value<string>("error"));
        }

        [Test]
        public void PropertyOrderAndAttemptDoNotChangeRequestIdentity()
        {
            Assert.IsNull(Admit());
            ledger.Start("first", 0);
            var response = ledger.Complete("first", 0, Success);
            var reordered = JObject.Parse("{\"code\":\"return 42;\",\"execution_id\":\"first\",\"action\":\"execute\",\"attempt\":4}");
            Assert.AreEqual(response, ledger.Admit("execute_code", reordered, out _, out _));
        }

        [Test]
        public void TimeoutRetainsLeaseAndLateCompletionRecoversIt()
        {
            Admit(); ledger.Start("first", 0);
            Assert.AreEqual("unknown", Result(ledger.CancelWait("first", 0))["data"].Value<string>("state"));
            Assert.AreEqual("execution_result_unknown", Result(Admit("new-id", "return 99;")).Value<string>("error"));
            now += 31000;
            Assert.AreEqual("execution_result_unknown", Result(Admit("another-id")).Value<string>("error"));
            ledger.Complete("first", 0, Success);
            Assert.IsNull(Admit("after-completion"));
        }

        [Test]
        public void CancellationBeforeStartNeverExecutesAndDoesNotHoldLease()
        {
            Admit();
            Assert.AreEqual("not_started", Result(ledger.CancelWait("first", 0))["data"].Value<string>("state"));
            Assert.IsFalse(ledger.Start("first", 0));
            Assert.IsNotNull(Admit());
            Assert.IsNull(Admit("new"));
        }

        [Test]
        public void UnknownExecutionSurvivesReloadAsInterrupted()
        {
            Admit(); ledger.Start("first", 0); ledger.CancelWait("first", 0);
            Reload();
            Assert.AreEqual("interrupted", State(ledger.Status("first")));
            Assert.IsFalse(Result(ledger.Status())["data"].Value<bool>("code_execution_blocked"));
            Assert.IsNull(Admit("new"));
        }

        [Test]
        public void StatusIsAvailableOnTransportThreadWhileExecutionIsBlocked()
        {
            Admit(); ledger.Start("first", 0); ledger.CancelWait("first", 0);
            string response = Task.Run(() => ledger.Status("first")).GetAwaiter().GetResult();
            Assert.AreEqual("unknown", State(response));
        }

        [Test]
        public void TransportThreadOperationsDoNotTouchTheStore()
        {
            int writes = store.Writes;
            Admit(); ledger.Status("other", true); ledger.CancelWait("first", 0);
            Assert.AreEqual(writes, store.Writes);
            Assert.IsTrue(ledger.IsDirty);
            ledger.Flush();
            Assert.IsFalse(ledger.IsDirty);
        }

        [Test]
        public void KnownCompilationIsBusyWithoutAdmission()
        {
            Assert.AreEqual("editor_busy", Result(ledger.Admit("execute_code", Request(), out _, out _, true)).Value<string>("error"));
            Assert.IsNull(Admit());
        }

        [Test]
        public void ExpiredResponseStillPreventsReexecution()
        {
            for (int i = 0; i < CommandExecutionLedger.RetainedResponses + 2; i++)
            {
                now++; string id = "request-" + i;
                Assert.IsNull(Admit(id)); ledger.Start(id, 0); ledger.Complete(id, 0, Success);
            }
            ledger.Flush();
            Assert.AreEqual("execution_result_not_retained", Result(Admit("request-0")).Value<string>("error"));
            Assert.AreEqual(CommandExecutionLedger.RetainedResponses,
                store.Values.Keys.Count(k => k.StartsWith(CommandExecutionLedger.ResponseKeyPrefix)));
        }

        [Test]
        public void HistoryIsBounded()
        {
            for (int i = 0; i < CommandExecutionLedger.RetainedEntries + 10; i++)
            {
                now++; string id = "request-" + i;
                Admit(id); ledger.Start(id, 0); ledger.Complete(id, 0, Success);
            }
            Reload();
            Assert.AreEqual("execution_not_found", Result(ledger.Status("request-0")).Value<string>("error"));
            Assert.AreEqual("completed", State(ledger.Status("request-" + (CommandExecutionLedger.RetainedEntries + 9))));
        }

        [TestCase("ping", null, false)]
        [TestCase("read_console", "get", false)]
        [TestCase("execute_code", "get_execution_status", false)]
        [TestCase("execute_code", "get_history", false)]
        [TestCase("execute_code", "clear_history", false)]
        [TestCase("execute_code", "execute", true)]
        [TestCase("execute_code", "replay", true)]
        [TestCase("execute_code", "EXECUTE", true)]
        [TestCase("execute_code", "unknown", false)]
        [TestCase("manage_editor", "play", false)]
        [TestCase("refresh_unity", null, false)]
        [TestCase("batch_execute", null, false)]
        [TestCase("execute_custom_tool", null, false)]
        public void OnlyCodeExecutionRequiresAdmission(string command, string action, bool expected)
        {
            Assert.AreEqual(expected, CommandExecutionLedger.RequiresCodeExecution(command, new JObject { ["action"] = action }));
        }

        [TestCase("manage_editor", "stop")]
        [TestCase("manage_scene", "get_hierarchy")]
        [TestCase("refresh_unity", null)]
        [TestCase("execute_code", "clear_history")]
        public void OrdinaryOperationsPassPendingCompilationAndUnknownCode(string command, string action)
        {
            Admit(); ledger.Start("first", 0); ledger.CancelWait("first", 0);
            Assert.IsNull(ledger.Admit(command, new JObject { ["action"] = action }, out var id, out _, true));
            Assert.IsNull(id);
        }

        private static JObject Batch(string tool, JObject parameters) => new JObject {
            ["commands"] = new JArray(new JObject { ["tool"] = tool, ["params"] = parameters })
        };

        [Test]
        public void NestedCodeWrappersRetainAdmissionAndReplayProtection()
        {
            var batch = Batch("batch_execute", Batch("execute_code", new JObject { ["action_"] = "replay", ["index"] = 0 }));
            Assert.IsTrue(CommandExecutionLedger.RequiresCodeExecution("batch_execute", batch));
            Assert.IsTrue(CommandExecutionLedger.RequiresCodeExecution("execute_custom_tool", new JObject {
                ["tool_name"] = "batch_execute", ["parameters"] = batch
            }));
            Assert.AreEqual("editor_busy", Result(ledger.Admit("batch_execute", batch, out _, out _, true)).Value<string>("error"));
            batch["execution_id"] = "batch";
            Assert.IsNull(ledger.Admit("batch_execute", batch, out var id, out var attempt));
            ledger.Start(id, attempt); ledger.CancelWait(id, attempt);
            Assert.IsNotNull(ledger.Admit("batch_execute", batch, out _, out _));
        }

        [Test]
        public void OrdinaryBatchesAndCustomToolsPassWithoutExecutionIdentity()
        {
            var batch = Batch("manage_asset", new JObject { ["action"] = "search", ["page_size"] = 10, ["pageSize"] = 10 });
            for (int i = 0; i < 12; i++) batch = Batch("batch_execute", batch);
            Assert.IsFalse(CommandExecutionLedger.RequiresCodeExecution("batch_execute", batch));
            Assert.IsNull(ledger.Admit("batch_execute", batch, out var id, out _, true));
            Assert.IsNull(id);
            Assert.IsFalse(ledger.IsDirty);
        }

        [Test]
        public void BatchClassificationUsesEffectiveParameterValues()
        {
            var args = new JObject { ["action"] = "execute", ["action_"] = "get_history" };
            Assert.IsFalse(CommandExecutionLedger.RequiresCodeExecution("batch_execute", Batch("execute_code", args)));
            args = new JObject { ["action_"] = "get_history", ["action"] = "execute" };
            Assert.IsTrue(CommandExecutionLedger.RequiresCodeExecution("batch_execute", Batch("execute_code", args)));
        }
    }
}
