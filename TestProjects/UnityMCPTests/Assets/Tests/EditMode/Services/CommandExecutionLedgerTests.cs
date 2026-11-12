using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Services
{
    public class CommandExecutionLedgerTests
    {
        private string directory;
        private string path;
        private long now;
        private CommandExecutionLedger ledger;
        private const string Success = "{\"status\":\"success\",\"result\":{\"success\":true,\"data\":{\"value\":42}}}";

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "unity-mcp-ledger-test-" + Guid.NewGuid().ToString("N"));
            path = Path.Combine(directory, "executions.json");
            now = 1000;
            ledger = new CommandExecutionLedger(path, "process-A", () => now);
        }

        [TearDown]
        public void TearDown() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

        private static JObject Request(string id = "first", string code = "return 42;") =>
            new JObject { ["action"] = "execute", ["code"] = code, ["execution_id"] = id };
        private static JObject Result(string response) => (JObject)JObject.Parse(response)["result"];
        private string Admit(string id = "first", string code = "return 42;") => ledger.Admit("execute_code", Request(id, code), out _);

        [Test]
        public void ConcurrentClientsAdmitOnlyOneMutation()
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
            Assert.IsTrue(ledger.Start("first"));
            string response = ledger.Complete("first", Success);
            Assert.AreEqual(response, Admit());
            Assert.AreEqual(42, Result(response)["data"].Value<int>("value"));
            Assert.IsTrue(Result(response)["execution"].Value<bool>("result_retained"));
        }

        [Test]
        public void ChangedArgumentsCannotReuseAnId()
        {
            Assert.IsNull(Admit());
            Assert.AreEqual("execution_id_conflict", Result(Admit(code: "return 99;")).Value<string>("error"));
        }

        [Test]
        public void PropertyOrderDoesNotChangeRequestIdentity()
        {
            Assert.IsNull(Admit());
            ledger.Start("first");
            var response = ledger.Complete("first", Success);
            var reordered = JObject.Parse("{\"code\":\"return 42;\",\"execution_id\":\"first\",\"action\":\"execute\"}");
            Assert.AreEqual(response, ledger.Admit("execute_code", reordered, out _));
        }

        [Test]
        public void TimeoutRetainsLeaseAndLateCompletionRecoversIt()
        {
            Admit(); ledger.Start("first");
            Assert.AreEqual("unknown", Result(ledger.CancelWait("first"))["data"].Value<string>("state"));
            Assert.AreEqual("execution_result_unknown", Result(Admit("new-id", "return 99;")).Value<string>("error"));
            ledger.Heartbeat();
            Assert.AreEqual("execution_result_unknown", Result(Admit("another-id")).Value<string>("error"));
            ledger.Complete("first", Success);
            Assert.IsNull(Admit("after-completion"));
        }

        [Test]
        public void RepeatedFailedProbesPreserveRecoveryByActualCompletion()
        {
            Admit(); ledger.Start("first");
            ledger.Unresponsive(); ledger.Unresponsive(); ledger.CancelWait("first");
            Assert.AreEqual("execution_result_unknown", Result(Admit("new")).Value<string>("error"));
            ledger.Complete("first", Success);
            Assert.IsNull(Admit("after-completion"));
        }

        [Test]
        public void CancellationBeforeStartNeverExecutesAndDoesNotHoldLease()
        {
            Admit();
            Assert.AreEqual("not_started", Result(ledger.CancelWait("first"))["data"].Value<string>("state"));
            Assert.IsFalse(ledger.Start("first"));
            Assert.IsNotNull(Admit());
            Assert.IsNull(Admit("new"));
        }

        [Test]
        public void FailedProbeStopsQueuedMutationFromStarting()
        {
            Admit(); ledger.Unresponsive();
            Assert.IsFalse(ledger.Start("first"));
            Assert.AreEqual("editor_unresponsive", Result(Admit("new")).Value<string>("error"));
        }

        [Test]
        public void UnresponsiveEditorRemainsBlockedAcrossHeartbeatAndDomainReload()
        {
            ledger.Unresponsive(); ledger.Heartbeat();
            Assert.AreEqual("editor_unresponsive", Result(Admit()).Value<string>("error"));
            ledger = new CommandExecutionLedger(path, "process-A", () => now);
            Assert.AreEqual("editor_unresponsive", Result(Admit()).Value<string>("error"));
            ledger = new CommandExecutionLedger(path, "process-B", () => now);
            Assert.IsNull(Admit());
        }

        [Test]
        public void UnknownExecutionSurvivesReloadAndRestartNeverReexecutesOldId()
        {
            Admit(); ledger.Start("first");
            ledger = new CommandExecutionLedger(path, "process-A", () => now);
            Assert.AreEqual("execution_result_unknown", Result(Admit("new")).Value<string>("error"));
            ledger = new CommandExecutionLedger(path, "process-B", () => now);
            Assert.AreEqual("interrupted", Result(ledger.Status("first"))["data"]["execution"].Value<string>("state"));
            Assert.IsNotNull(Admit());
            Assert.IsNull(Admit("new"));
        }

        [Test]
        public void StatusIsAvailableOnTransportThreadWhileExecutionIsBlocked()
        {
            Admit(); ledger.Start("first"); ledger.CancelWait("first");
            string response = Task.Run(() => ledger.Status("first")).GetAwaiter().GetResult();
            Assert.AreEqual("unknown", Result(response)["data"]["execution"].Value<string>("state"));
        }

        [Test]
        public void MissingHeartbeatBlocksNewMutations()
        {
            now += 31000;
            Assert.AreEqual("editor_unresponsive", Result(Admit()).Value<string>("error"));
        }

        [Test]
        public void KnownCompilationDoesNotLatchAnUnresponsiveFault()
        {
            now += 31000;
            Assert.AreEqual("editor_busy", Result(ledger.Admit("execute_code", Request(), out _, true)).Value<string>("error"));
            ledger.Heartbeat();
            Assert.IsNull(Admit());
        }

        [Test]
        public void CorruptJournalBlocksChanges()
        {
            Directory.CreateDirectory(directory); File.WriteAllText(path, "broken json");
            ledger = new CommandExecutionLedger(path, "process-A", () => now);
            Assert.AreEqual("execution_storage_error", Result(Admit()).Value<string>("error"));
        }

        [Test]
        public void ExpiredResponseStillPreventsReexecution()
        {
            for (int i = 0; i < 66; i++)
            {
                now++; string id = "request-" + i;
                Assert.IsNull(Admit(id)); ledger.Start(id); ledger.Complete(id, Success);
            }
            Assert.AreEqual("execution_result_not_retained", Result(Admit("request-0")).Value<string>("error"));
        }

        [TestCase("ping", null, true)]
        [TestCase("read_console", "get", true)]
        [TestCase("read_console", "clear", false)]
        [TestCase("execute_code", "get_execution_status", true)]
        [TestCase("execute_code", "replay", false)]
        [TestCase("execute_code", "clear_history", false)]
        [TestCase("batch_execute", null, false)]
        [TestCase("execute_custom_tool", null, false)]
        public void MutationClassificationDefaultsToExclusive(string command, string action, bool expected)
        {
            Assert.AreEqual(expected, CommandExecutionLedger.IsReadOnly(command, new JObject { ["action"] = action }));
        }
    }
}
