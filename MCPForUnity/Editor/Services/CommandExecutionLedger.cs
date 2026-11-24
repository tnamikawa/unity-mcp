using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MCPForUnity.Editor.Services
{
    /// <summary>
    /// Key-value storage that survives assembly reloads but not an Editor restart
    /// (SessionState in the Editor). Called only from <see cref="CommandExecutionLedger.Flush"/>
    /// and the constructor, both on the Unity main thread.
    /// </summary>
    internal interface IExecutionJournalStore
    {
        string Read(string key);
        void Write(string key, string value);
        void Erase(string key);
    }

    // One ledger per Editor process, shared by every transport and client.
    //
    // Lifecycle model:
    // - A domain reload unloads all managed state, including queued commands and the socket
    //   that carried them. The record therefore lives in a store that survives reloads, and a
    //   command that was only queued when the domain unloaded can never start afterwards.
    // - "running" is written to that store before the handler starts, so a reload or restart
    //   after that point reports the execution as interrupted, never as unstarted.
    // - An Editor restart clears the store. Executions that the previous process may have
    //   received are reported as "lost" rather than guessed.
    // - Admission, status and cancellation run on transport threads and only touch memory;
    //   the store is written from the main thread (Start, Flush), so a write failure is
    //   retried on the next flush instead of disabling the ledger.
    internal sealed class CommandExecutionLedger
    {
        internal const string DocumentKey = "MCPForUnity.Execution.Journal";
        internal const string ResponseKeyPrefix = "MCPForUnity.Execution.Response.";
        internal const int RetainedEntries = 512;
        internal const int RetainedResponses = 16;
        internal const int MaxResponseLength = 262144;
        internal const int FinalAttempt = int.MaxValue;
        private const string SettledFingerprint = "settled-unstarted";
        // Process.StartTime and the server clock are compared on one machine; allow for rounding.
        private const long ProcessStartToleranceMs = 2000;

        internal sealed class Entry
        {
            public string Id;
            public string Fingerprint;
            public string Command;
            public string State;
            public long StartedAt;
            public int Attempt;
            public bool HasResponse;
            [JsonIgnore] public string Response;
            [JsonIgnore] public bool LegacyAdmission;
        }

        private sealed class Document
        {
            public string Owner;
            public string BlockedReason;
            public List<Entry> Entries = new();
        }

        private readonly object sync = new();
        private readonly IExecutionJournalStore store;
        private readonly string owner;
        private readonly long processStartedAt;
        private readonly Func<long> clock;
        private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
        private readonly HashSet<string> responsesToWrite = new(StringComparer.Ordinal);
        private readonly HashSet<string> responsesToErase = new(StringComparer.Ordinal);
        private string blockedReason;
        private string activeId;
        private bool dirty;
        private bool historyIncomplete;
        private string lastStoreError;

        internal CommandExecutionLedger(IExecutionJournalStore store, string owner, long processStartedAt, Func<long> clock = null)
        {
            this.store = store;
            this.owner = owner;
            this.processStartedAt = processStartedAt;
            this.clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Load();
        }

        private void Load()
        {
            string json;
            try { json = store.Read(DocumentKey); }
            catch (Exception ex)
            {
                lastStoreError = ex.Message;
                historyIncomplete = true;
                return;
            }
            if (json == null) return;
            Document saved = null;
            try { saved = JsonConvert.DeserializeObject<Document>(json); }
            catch (JsonException ex) { lastStoreError = ex.Message; }
            if (saved?.Entries == null)
            {
                // Unknown history: unknown IDs can no longer be proven unstarted.
                historyIncomplete = true;
                dirty = true;
                return;
            }
            foreach (var entry in saved.Entries)
            {
                if (entry?.Id == null || entries.ContainsKey(entry.Id)) continue;
                entries.Add(entry.Id, entry);
                if (entry.HasResponse)
                {
                    try { entry.Response = store.Read(ResponseKeyPrefix + entry.Id); }
                    catch (Exception ex) { lastStoreError = ex.Message; }
                    entry.HasResponse = entry.Response != null;
                }
                if (IsOutstanding(entry))
                {
                    // A new ledger instance belongs to a newly loaded domain. Queued commands
                    // died with the old domain; a handler that had started did not finish here.
                    entry.State = entry.State == "queued" ? "not_started" : "interrupted";
                    dirty = true;
                }
            }
            // A result-unknown block refers to an execution that is now interrupted.
            if (saved.BlockedReason != null) dirty = true;
        }

        internal static bool RequiresCodeExecution(string command, JObject parameters)
        {
            var pending = new Stack<(string Tool, JObject Parameters)>();
            pending.Push((command, parameters));
            while (pending.Count > 0)
            {
                var item = pending.Pop();
                if (item.Parameters == null) continue;
                if (item.Tool == "execute_code")
                {
                    var action = item.Parameters["action"];
                    if (action?.Type == JTokenType.String &&
                        (string.Equals((string)action, "execute", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals((string)action, "replay", StringComparison.OrdinalIgnoreCase))) return true;
                }
                else if (item.Tool == "batch_execute" && item.Parameters["commands"] is JArray commands)
                {
                    foreach (var child in commands.OfType<JObject>())
                    {
                        var normalized = new JObject();
                        if (child["params"] is JObject raw)
                            foreach (var property in raw.Properties())
                            {
                                var parts = property.Name.Split('_');
                                string name = parts[0] + string.Concat(parts.Skip(1).Where(p => p.Length > 0)
                                    .Select(p => char.ToUpperInvariant(p[0]) + p.Substring(1)));
                                normalized[name] = property.Value.DeepClone();
                            }
                        pending.Push((child["tool"]?.ToString(), normalized));
                    }
                }
                else if (item.Tool == "execute_custom_tool")
                    pending.Push((item.Parameters["tool_name"]?.ToString(), item.Parameters["parameters"] as JObject));
            }
            return false;
        }

        /// <summary>
        /// Admits one code execution. The server numbers each send of the same execution ID
        /// ("attempt"); an ID settled as unstarted is admitted again only by a later attempt,
        /// so a delayed copy of an abandoned send can never start.
        /// </summary>
        internal string Admit(string command, JObject parameters, out string id, out int attempt, bool compilationPending = false)
        {
            id = null;
            attempt = 0;
            if (!RequiresCodeExecution(command, parameters)) return null;
            lock (sync)
            {
                id = parameters?.Value<string>("execution_id") ?? Guid.NewGuid().ToString("N");
                if (string.IsNullOrWhiteSpace(id) || id.Length > 128)
                    return Error("invalid_execution_id", "Use an execution ID containing 1 to 128 characters.");
                bool legacy = parameters?["attempt"] == null;
                attempt = legacy ? 0 : Math.Max(0, Math.Min(FinalAttempt - 1, parameters.Value<int>("attempt")));
                string fingerprint = Fingerprint(command, parameters);
                entries.TryGetValue(id, out var previous);
                if (previous != null)
                {
                    if (previous.Fingerprint != SettledFingerprint && previous.Fingerprint != fingerprint)
                        return Error("execution_id_conflict", "This execution ID belongs to a different request.", previous);
                    if (previous.State != "not_started" || attempt <= previous.Attempt)
                        return previous.Response ?? Error(
                            IsOutstanding(previous) ? "execution_in_progress"
                                : previous.State == "not_started" ? "execution_not_started"
                                : previous.State == "lost" ? "execution_lost" : "execution_result_not_retained",
                            "Inspect the recorded execution. This request has not been executed again.", previous);
                }
                if (blockedReason != null || activeId != null)
                    return Error(blockedReason ?? "execution_in_progress", "C# execution is blocked. Inspect the existing execution.", Active());
                if (compilationPending)
                    return Error("editor_busy", "Compilation or assembly reload is pending. Inspect editor readiness.");
                var entry = previous ?? new Entry { Id = id, Command = command };
                entry.Fingerprint = fingerprint;
                entry.State = "queued";
                entry.StartedAt = clock();
                entry.Attempt = attempt;
                entry.LegacyAdmission = legacy;
                entries[id] = entry;
                activeId = id;
                dirty = true;
                return null;
            }
        }

        /// <summary>Main thread only. Records "running" in the store before the handler starts.</summary>
        internal bool Start(string id, int attempt)
        {
            lock (sync)
            {
                if (!entries.TryGetValue(id, out var entry) || entry.State != "queued"
                    || entry.Attempt != attempt || blockedReason != null) return false;
                entry.State = "running";
                dirty = true;
                if (FlushLocked()) return true;
                entry.State = "queued";
                return false;
            }
        }

        internal string Complete(string id, int attempt, string response)
        {
            lock (sync)
            {
                // A superseded copy, or a copy that was settled before its handler started.
                if (!entries.TryGetValue(id, out var entry) || entry.Attempt != attempt || entry.State == "not_started")
                    return response;
                var deferred = JObject.Parse(response)["result"] as JObject;
                if (entry.State == "queued" && deferred?.Value<string>("error") == "editor_busy")
                {
                    // Declined before start; the same send may be repeated as a later attempt.
                    if (entry.LegacyAdmission) entries.Remove(id);
                    else entry.State = "not_started";
                    if (activeId == id) activeId = null;
                    dirty = true;
                    return response;
                }
                if (IsOutstanding(entry)) entry.State = "completed";
                var envelope = JObject.Parse(response);
                var result = envelope["result"] as JObject ?? envelope;
                result["execution"] = Describe(entry, response.Length <= MaxResponseLength);
                response = envelope.ToString(Formatting.None);
                SetResponse(entry, response.Length <= MaxResponseLength ? response : null);
                if (activeId == id)
                {
                    activeId = null;
                    if (blockedReason == "execution_result_unknown") blockedReason = null;
                }
                Prune();
                dirty = true;
                return response;
            }
        }

        internal string CancelWait(string id, int attempt)
        {
            lock (sync)
            {
                if (!entries.TryGetValue(id, out var entry) || entry.Attempt != attempt)
                    return Error("execution_not_started", "This send was superseded and has not been executed.");
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
                dirty = true;
                return entry.Response ?? Error(entry.State == "not_started" ? "execution_not_started" : "execution_result_unknown",
                    "The response wait ended. Query this execution ID; further C# execution is blocked while its result is unknown.", entry);
            }
        }

        /// <param name="settleUnstarted">Guarantee that sends up to <paramref name="settleAttempt"/> never start.</param>
        /// <param name="settleAttempt">Highest abandoned attempt; omitted means no further attempt will follow.</param>
        /// <param name="admittedAt">When the caller admitted the request (Unix ms); a process started
        /// after that moment cannot tell whether an earlier process received it.</param>
        internal string Status(string id = null, bool settleUnstarted = false, int? settleAttempt = null, long? admittedAt = null)
        {
            lock (sync)
            {
                if (id != null)
                {
                    int abandoned = settleAttempt ?? FinalAttempt;
                    if (!entries.TryGetValue(id, out var entry))
                    {
                        if (!settleUnstarted)
                            return Error("execution_not_found", "No execution has been admitted with this ID.");
                        bool lost = historyIncomplete || (admittedAt.HasValue && processStartedAt >= admittedAt.Value - ProcessStartToleranceMs);
                        // Reserve the ID atomically: a delayed transport copy can never start
                        // after the caller has been told this request did not run here.
                        entry = new Entry { Id = id, Command = "execute_code", Fingerprint = SettledFingerprint,
                            State = lost ? "lost" : "not_started", StartedAt = clock(), Attempt = lost ? FinalAttempt : abandoned };
                        entries.Add(id, entry);
                        dirty = true;
                    }
                    else if (settleUnstarted && (entry.State == "queued" || entry.State == "not_started"))
                    {
                        entry.State = "not_started";
                        entry.Attempt = Math.Max(entry.Attempt, abandoned);
                        if (activeId == id) activeId = null;
                        dirty = true;
                    }
                    return JsonConvert.SerializeObject(new { status = "success", result = new {
                        success = true, data = new { execution = Describe(entry), response = entry.Response == null ? null : JToken.Parse(entry.Response) }
                    }});
                }
                return JsonConvert.SerializeObject(new { status = "success", result = new {
                    success = true, data = new {
                        editor_process = owner, process_started_at_unix_ms = processStartedAt, blocked_reason = blockedReason,
                        code_execution_blocked = blockedReason != null || activeId != null,
                        history_incomplete = historyIncomplete, journal_error = lastStoreError,
                        active_execution = Active() == null ? null : Describe(Active()),
                        recent_executions = entries.Values.OrderByDescending(e => e.StartedAt).Take(20).Select(e => Describe(e)).ToArray()
                    }
                }});
            }
        }

        /// <summary>Main thread only. Writes pending changes; a failure is retried on the next call.</summary>
        internal bool Flush()
        {
            lock (sync) return FlushLocked();
        }

        internal bool IsDirty
        {
            get { lock (sync) return dirty; }
        }

        private bool FlushLocked()
        {
            if (!dirty) return true;
            try
            {
                foreach (var key in responsesToErase) store.Erase(ResponseKeyPrefix + key);
                responsesToErase.Clear();
                foreach (var key in responsesToWrite)
                    if (entries.TryGetValue(key, out var entry) && entry.Response != null)
                        store.Write(ResponseKeyPrefix + key, entry.Response);
                responsesToWrite.Clear();
                store.Write(DocumentKey, JsonConvert.SerializeObject(new Document {
                    Owner = owner, BlockedReason = blockedReason, Entries = entries.Values.ToList()
                }));
                dirty = false;
                lastStoreError = null;
                return true;
            }
            catch (Exception ex)
            {
                lastStoreError = ex.Message;
                return false;
            }
        }

        private void SetResponse(Entry entry, string response)
        {
            entry.Response = response;
            entry.HasResponse = response != null;
            responsesToErase.Remove(entry.Id);
            responsesToWrite.Remove(entry.Id);
            if (response != null) responsesToWrite.Add(entry.Id);
            else responsesToErase.Add(entry.Id);
        }

        private void Prune()
        {
            var settled = entries.Values.Where(e => !IsOutstanding(e)).OrderByDescending(e => e.StartedAt).ToList();
            foreach (var old in settled.Where(e => e.Response != null).Skip(RetainedResponses).ToList())
                SetResponse(old, null);
            foreach (var old in settled.Skip(RetainedEntries))
            {
                entries.Remove(old.Id);
                responsesToWrite.Remove(old.Id);
                if (old.HasResponse) responsesToErase.Add(old.Id);
            }
        }

        private Entry Active() => activeId != null && entries.TryGetValue(activeId, out var active) ? active : null;
        private static bool IsOutstanding(Entry entry) => entry.State == "queued" || entry.State == "running" || entry.State == "unknown";
        private static JObject Describe(Entry entry, bool? retained = null) => new JObject {
            ["execution_id"] = entry.Id, ["command"] = entry.Command, ["state"] = entry.State,
            ["attempt"] = entry.Attempt, ["started_at_unix_ms"] = entry.StartedAt,
            ["result_retained"] = retained ?? entry.Response != null
        };

        private static string Error(string error, string message, Entry entry = null) => JsonConvert.SerializeObject(new {
            status = "success", result = new {
                success = false, error, message, hint = "inspect_execution",
                data = entry == null ? null : Describe(entry)
            }
        });

        private static string Fingerprint(string command, JObject parameters)
        {
            var copy = parameters == null ? new JObject() : (JObject)parameters.DeepClone();
            copy.Remove("execution_id");
            copy.Remove("attempt");
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
