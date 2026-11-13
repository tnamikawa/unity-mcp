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
            now += 31000;
            Assert.AreEqual("execution_result_unknown", Result(Admit("another-id")).Value<string>("error"));
            ledger.Complete("first", Success);
            Assert.IsNull(Admit("after-completion"));
        }

        [Test]
        public void RepeatedCancelledWaitsPreserveRecoveryByActualCompletion()
        {
            Admit(); ledger.Start("first");
            ledger.CancelWait("first"); ledger.CancelWait("first");
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
        public void LegacyDiagnosticFaultAndOrdinaryEntriesAreRemovedOnReload()
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, "{\"Owner\":\"process-A\",\"BlockedReason\":\"editor_unresponsive\",\"Entries\":[" +
                "{\"Id\":\"old-stop\",\"Command\":\"manage_editor\",\"State\":\"unknown\",\"Fingerprint\":\"old\"}]}");
            ledger = new CommandExecutionLedger(path, "process-A", () => now);
            Assert.IsFalse(Result(ledger.Status())["data"].Value<bool>("code_execution_blocked"));
            Assert.IsEmpty((JArray)JObject.Parse(File.ReadAllText(path))["Entries"]);
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
        public void IdleTimeDoesNotBlockCodeAdmission()
        {
            now += 31000;
            Assert.IsNull(Admit());
        }

        [Test]
        public void KnownCompilationDoesNotLatchAnUnresponsiveFault()
        {
            now += 31000;
            Assert.AreEqual("editor_busy", Result(ledger.Admit("execute_code", Request(), out _, true)).Value<string>("error"));
            now += 31000;
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

        [TestCase("ping", null, false)]
        [TestCase("read_console", "get", false)]
        [TestCase("read_console", "clear", false)]
        [TestCase("execute_code", "get_execution_status", false)]
        [TestCase("execute_code", "get_history", false)]
        [TestCase("execute_code", "clear_history", false)]
        [TestCase("execute_code", "execute", true)]
        [TestCase("execute_code", "replay", true)]
        [TestCase("execute_code", "EXECUTE", true)]
        [TestCase("execute_code", "unknown", false)]
        [TestCase("manage_editor", "stop", false)]
        [TestCase("manage_editor", "play", false)]
        [TestCase("manage_editor", "telemetry_status", false)]
        [TestCase("manage_scene", "get_hierarchy", false)]
        [TestCase("manage_asset", "search", false)]
        [TestCase("refresh_unity", null, false)]
        [TestCase("get_project_info", null, false)]
        [TestCase("batch_execute", null, false)]
        [TestCase("execute_custom_tool", null, false)]
        public void OnlyCodeExecutionRequiresAdmission(string command, string action, bool expected)
        {
            Assert.AreEqual(expected, CommandExecutionLedger.RequiresCodeExecution(command, new JObject { ["action"] = action }));
        }

        [TestCase("manage_editor", "stop")]
        [TestCase("manage_editor", "play")]
        [TestCase("manage_scene", "get_hierarchy")]
        [TestCase("manage_asset", "search")]
        [TestCase("refresh_unity", null)]
        [TestCase("get_project_info", null)]
        [TestCase("execute_code", "clear_history")]
        public void OrdinaryOperationsPassPendingCompilationAndUnknownCodeWithoutJournalWrites(string command, string action)
        {
            Admit(); ledger.Start("first"); ledger.CancelWait("first");
            var before = File.ReadAllBytes(path);
            Assert.IsNull(ledger.Admit(command, new JObject { ["action"] = action }, out var id, true));
            Assert.IsNull(id);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
            File.WriteAllText(path, "corrupt");
            ledger = new CommandExecutionLedger(path, "process-A", () => now);
            Assert.IsNull(ledger.Admit(command, new JObject { ["action"] = action }, out id, true));
            Assert.IsNull(id);
            Assert.AreEqual("corrupt", File.ReadAllText(path));
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
            Assert.AreEqual("editor_busy", Result(ledger.Admit("batch_execute", batch, out _, true)).Value<string>("error"));
            batch["execution_id"] = "batch";
            Assert.IsNull(ledger.Admit("batch_execute", batch, out var id));
            ledger.Start(id); ledger.CancelWait(id);
            Assert.IsNotNull(ledger.Admit("batch_execute", batch, out _));
        }

        [Test]
        public void OrdinaryBatchesAndCustomToolsPassWithoutExecutionIdentity()
        {
            var batch = Batch("manage_asset", new JObject { ["action"] = "search", ["page_size"] = 10, ["pageSize"] = 10 });
            for (int i = 0; i < 12; i++) batch = Batch("batch_execute", batch);
            Assert.IsFalse(CommandExecutionLedger.RequiresCodeExecution("batch_execute", batch));
            Assert.IsNull(ledger.Admit("batch_execute", batch, out var id, true));
            Assert.IsNull(id);
            Assert.IsNull(ledger.Admit("execute_custom_tool", new JObject {
                ["tool_name"] = "manage_editor", ["parameters"] = new JObject { ["action"] = "stop" }
            }, out id, true));
            Assert.IsNull(id);
            Assert.IsFalse(File.Exists(path));
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
