using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MCPForUnity.Editor.Services
{
    // One ledger per Editor project/process, shared by every transport and client.
    // Contains no Unity API calls; status and rejection remain available off the main thread.
    internal sealed class CommandExecutionLedger
    {
        internal sealed class Entry
        {
            public string Id;
            public string Fingerprint;
            public string Command;
            public string State;
            public long StartedAt;
            public string Response;
        }

        private sealed class Document
        {
            public string Owner;
            public string BlockedReason;
            public List<Entry> Entries = new();
        }

        private readonly object sync = new();
        private readonly string path;
        private readonly string owner;
        private readonly Func<long> clock;
        private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
        private string blockedReason;
        private string activeId;
        private string storageError;
        private long lastHeartbeat;

        internal CommandExecutionLedger(string path, string owner, Func<long> clock = null)
        {
            this.path = path;
            this.owner = owner;
            this.clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            lastHeartbeat = this.clock();
            try
            {
                if (!File.Exists(path)) return;
                var saved = JsonConvert.DeserializeObject<Document>(File.ReadAllText(path));
                if (saved == null || saved.Owner == null || saved.Entries == null)
                    throw new InvalidDataException("Invalid execution ledger");
                bool restarted = saved.Owner != owner;
                blockedReason = restarted ? null : saved.BlockedReason;
                foreach (var entry in saved.Entries)
                {
                    entries.Add(entry.Id, entry);
                    if (IsOutstanding(entry))
                    {
                        entry.State = restarted ? "interrupted" : "unknown";
                        entry.Response = null;
                        if (!restarted)
                        {
                            activeId = entry.Id;
                            blockedReason = "execution_result_unknown";
                        }
                    }
                }
                Save();
            }
            catch (Exception ex) { storageError = ex.Message; }
        }

        internal static bool IsReadOnly(string command, JObject parameters)
        {
            string action = parameters?.Value<string>("action")?.ToLowerInvariant();
            switch (command?.ToLowerInvariant())
            {
                case "ping": case "get_editor_state": case "get_test_job": return true;
                case "read_console": return action == null || action == "get";
                case "execute_code": return action == "get_history" || action == "get_execution_status";
                default: return false; // Custom tools and batches also acquire the mutation lease.
            }
        }

        internal void Heartbeat() { lock (sync) lastHeartbeat = clock(); }

        internal string Admit(string command, JObject parameters, out string id, bool compilationPending = false)
        {
            lock (sync)
            {
                id = parameters?.Value<string>("execution_id") ?? Guid.NewGuid().ToString("N");
                if (string.IsNullOrWhiteSpace(id) || id.Length > 128)
                    return Error("invalid_execution_id", "Use an execution ID containing 1 to 128 characters.");
                string fingerprint = Fingerprint(command, parameters);
                if (entries.TryGetValue(id, out var previous))
                {
                    if (previous.Fingerprint != fingerprint)
                        return Error("execution_id_conflict", "This execution ID belongs to a different request.", previous);
                    return previous.Response ?? Error(IsOutstanding(previous) ? "execution_in_progress" : "execution_result_not_retained",
                        "Inspect the recorded execution. This request has not been executed again.", previous);
                }
                if (storageError != null)
                    return Error("execution_storage_error", "Execution journal unavailable: " + storageError);
                if (blockedReason != null || activeId != null)
                    return Error(blockedReason ?? "execution_in_progress", "Changes are blocked. Inspect the existing execution.", Active());
                if (compilationPending)
                    return Error("editor_busy", "Compilation or assembly reload is pending. Inspect editor readiness.");
                if (clock() - lastHeartbeat > 30000)
                {
                    blockedReason = "editor_unresponsive";
                    Save();
                    return Error(blockedReason, "The Editor main thread has stopped responding. Restart the Editor to recover.");
                }
                var entry = new Entry { Id = id, Fingerprint = fingerprint, Command = command,
                    State = "queued", StartedAt = clock() };
                entries.Add(id, entry);
                activeId = id;
                if (!Save()) return Error("execution_storage_error", "Could not persist the execution before dispatch.", entry);
                return null;
            }
        }

        internal bool Start(string id)
        {
            lock (sync)
            {
                var entry = entries[id];
                if (entry.State != "queued" || blockedReason != null || storageError != null) return false;
                entry.State = "running";
                return Save();
            }
        }

        internal string Complete(string id, string response)
        {
            lock (sync)
            {
                var entry = entries[id];
                if (IsOutstanding(entry)) entry.State = "completed";
                entry.Response = response.Length <= 262144 ? response : null;
                var envelope = JObject.Parse(response);
                var result = envelope["result"] as JObject ?? envelope;
                result["execution"] = Describe(entry);
                response = envelope.ToString(Formatting.None);
                entry.Response = response.Length <= 262144 ? response : null;
                if (activeId == id)
                {
                    activeId = null;
                    if (blockedReason == "execution_result_unknown") blockedReason = null;
                }
                // Retain IDs/fingerprints forever; old IDs never silently become new executions.
                foreach (var old in entries.Values.Where(e => e.Response != null)
                    .OrderByDescending(e => e.StartedAt).Skip(64)) old.Response = null;
                Save();
                return response;
            }
        }

        internal string CancelWait(string id)
        {
            lock (sync)
            {
                var entry = entries[id];
                if (entry.State == "queued")
                {
                    entry.State = "not_started";
                    if (activeId == id) activeId = null;
                }
                else if (IsOutstanding(entry))
                {
                    entry.State = "unknown";
                    blockedReason = "execution_result_unknown";
                }
                Save();
                return entry.Response ?? Error(entry.State == "not_started" ? "execution_not_started" : "execution_result_unknown",
                    "The response wait ended. Query this execution ID; changes remain blocked while its result is unknown.", entry);
            }
        }

        internal void Unresponsive()
        {
            lock (sync)
            {
                var active = Active();
                if (active != null && (active.State == "running" || active.State == "unknown"))
                {
                    active.State = "unknown";
                    blockedReason = "execution_result_unknown";
                }
                else blockedReason = "editor_unresponsive";
                Save();
            }
        }

        internal string Status(string id = null)
        {
            lock (sync)
            {
                if (id != null)
                {
                    if (!entries.TryGetValue(id, out var entry))
                        return Error("execution_not_found", "No execution has been admitted with this ID.");
                    return JsonConvert.SerializeObject(new { status = "success", result = new {
                        success = true, data = new { execution = Describe(entry), response = entry.Response == null ? null : JToken.Parse(entry.Response) }
                    }});
                }
                return JsonConvert.SerializeObject(new { status = "success", result = new {
                    success = true, data = new {
                        editor_process = owner, blocked_reason = storageError != null ? "execution_storage_error" : blockedReason,
                        changes_blocked = storageError != null || blockedReason != null || activeId != null,
                        active_execution = Active() == null ? null : Describe(Active()),
                        recent_executions = entries.Values.OrderByDescending(e => e.StartedAt).Take(20).Select(Describe).ToArray()
                    }
                }});
            }
        }

        private Entry Active() => activeId != null && entries.TryGetValue(activeId, out var active) ? active : null;
        private static bool IsOutstanding(Entry entry) => entry.State == "queued" || entry.State == "running" || entry.State == "unknown";
        private static JObject Describe(Entry entry) => new JObject {
            ["execution_id"] = entry.Id, ["command"] = entry.Command, ["state"] = entry.State,
            ["started_at_unix_ms"] = entry.StartedAt, ["result_retained"] = entry.Response != null
        };

        private static string Error(string error, string message, Entry entry = null) => JsonConvert.SerializeObject(new {
            status = "success", result = new {
                success = false, error, message, hint = "inspect_execution",
                data = entry == null ? null : Describe(entry)
            }
        });

        private bool Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string staging = path + ".pending";
                File.WriteAllText(staging, JsonConvert.SerializeObject(new Document {
                    Owner = owner, BlockedReason = blockedReason, Entries = entries.Values.ToList()
                }), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(staging, path, null);
                else File.Move(staging, path);
                return true;
            }
            catch (Exception ex) { storageError = ex.Message; return false; }
        }

        private static string Fingerprint(string command, JObject parameters)
        {
            var copy = parameters == null ? new JObject() : (JObject)parameters.DeepClone();
            copy.Remove("execution_id");
            using var hash = SHA256.Create();
            return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(command.ToLowerInvariant() + "\n" + Canonical(copy))));
        }

        private static string Canonical(JToken token)
        {
            if (token is JObject obj) return "{" + string.Join(",", obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => JsonConvert.ToString(p.Name) + ":" + Canonical(p.Value))) + "}";
            if (token is JArray array) return "[" + string.Join(",", array.Select(Canonical)) + "]";
            return token.ToString(Formatting.None);
        }
    }
}
