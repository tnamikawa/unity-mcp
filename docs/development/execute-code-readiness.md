# Execute code admission and completion

## Scope

Python owns one admission slot per Unity instance for `execute_code` actions
`execute` and `replay`, including code-bearing `batch_execute` and
`execute_custom_tool` wrappers. A wrapper is one execution unit, and its complete
handler result is returned. Code-free wrappers, queries, ping, Play/Stop, refresh,
ordinary transport ordering, cancellation and stdio behavior use the existing
transport path. The `refresh_unity` readiness conditions are unchanged.

Luna independent review remains mandatory in the existing client hooks. A refused
or failed review stops execution. This change preserves those hooks and the
compilation guard release on `afterAssemblyReload` from `b65e04b7`.

## Ownership and reconnecting clients

The instance key is the resolved project hash, qualified by the authenticated
user for remote hosting. Aliases (`Name@hash`, project name, hash prefix) resolve
to that key before admission. HTTP and stdio processes on the same machine share
`~/.unity-mcp/code-execution-slots.sqlite3`. SQLite's unique active-slot index and
transactions arbitrate admission; the database stores ownership and results, never
submitted C# source. It is runtime state, with Unity's journal providing the
execution record.

An occupied slot is matched only by its owning MCP session. HTTP uses
`MCP-Session-Id`; stdio uses FastMCP's per-connection UUID (`ctx.session_id`).
The user approved this interpretation on 2026-11-18. The same session attaches to
the occupied operation even when its source or supplied execution ID changes.
A new MCP session is a separate owner. The local REST convenience endpoint
`/api/command` has no MCP session and rejects code-bearing requests, including
batches and custom wrappers, before Unity discovery. Its ordinary command path
is unchanged. An unavailable session identity fails
closed with `code_execution_session_required` before admission.

Another session immediately receives `code_execution_slot_occupied`, with the
occupying execution ID, waiting/running/reconciling phase, and elapsed seconds.
There is no queue and no automatic resubmission of code after an uncertain send.
MCP control envelopes omit retry hints and retry-after fields. C# return
values under `data.result` retain their original fields and content.

Workers outlive individual MCP requests. Losing an MCP response or cancelling a
waiter does not cancel the worker or release its slot. A 5-second ownership
heartbeat arbitrates server-process recovery. After 30 seconds without that
heartbeat, a subsequent request can start journal reconciliation of the abandoned
slot; it never dispatches its old source. A different session is still rejected
immediately while reconciliation runs. A heartbeat expiry itself never establishes
that Unity execution has ended.

## Readiness and completion

Python asynchronously probes `get_editor_state` before dispatch and applies the
existing Python advice/staleness enrichment to the raw Unity snapshot. The
instance middleware skips reconnect validation specifically for code-bearing
calls, so slot admission and foreign-session refusal precede reconnect waits.
Concrete `Name@hash` routes remain usable during reload. Code-bearing batches
skip the Python configured-limit Editor lookup before admission, retain the hard
100-command ceiling, and use the existing Unity-side configured-limit check.
Code-free batches keep their existing Python preflight. It waits for
`advice.ready_for_code_execution`: compilation, pending compilation/reload,
Play Mode transition and asset refresh/import block code dispatch. Stable Play
Mode remains supported. `UNITY_MCP_CODE_IDLE_TIMEOUT_SECONDS` configures this
wait (default 600 seconds; a positive finite value is required).

Idle expiry atomically marks the server slot completed with
`code_idle_wait_timeout`, `state=not_started`, `execution_started=false`. The
worker checks its deadline and ownership before dispatch; an expired request has
no queued execute task that could run later. A readiness probe can be cancelled
at the deadline because it only reads state.

Unity checks readiness once again on the main thread immediately before starting
a code-bearing handler. A busy race defers that queued admission without starting
it, allowing Python to wait again using the same execution ID. Unity adds no
readiness polling or waiting loop. The existing handler-level guard also checks
before compilation and invocation, independently of `safety_checks`.

The Python call returns when the handler completes, with its success or failure.
Existing transport response deadlines can end a single bridge wait. Python then
keeps the slot and reconciles Unity's journal instead of reporting a transient
unknown result as the final MCP response. Ordinary bridge deadlines and
cancellation behavior are preserved.

Every 12 seconds each attached request calls `ctx.report_progress`, including the
phase and occupying execution ID. MCP progress notifications require the client's
request `_meta.progressToken`. The MCP client integration test verifies tokened
notifications in both waiting and running phases. A notification delivery failure
has no effect on worker ownership. Notification support and overall client
execution deadlines remain separate client capabilities.

## Unity journal and recovery

`Library/MCPForUnity/command-executions.json` remains the execution journal.
Admission is persisted before dispatch. IDs and fingerprints retain their existing
replay protection. Complete responses are retained for the latest 64 eligible
completions, up to 256 Ki-characters each. Journal storage errors block execution.

After a failed response wait or reconnect, Python queries
`execute_code(action="get_execution_status", execution_id=...)`. Its internal
`settle_unstarted=true` option atomically settles an absent or queued execution as
`not_started`. An absent ID receives a durable tombstone; a delayed send cannot
admit it afterward. Settling an already running/unknown execution leaves it active.
The public status query keeps its existing read-only behavior without that option.

- A completed journal entry supplies its stored result and releases the slot.
- A settled unstarted entry releases it with `execution_not_started`.
- Running/unknown entries retain it and continue reconciliation.
- A new Unity process or AppDomain marks discarded queued work `not_started` and
  work that had started `interrupted`. Old-domain tasks have ended; their IDs stay
  reserved. Reconciliation releases these slots with `execution_interrupted`.

Transport unresponsiveness alone never changes those journal states. Recovery
operations remain eligible while a code slot is occupied. Unity processes their
existing command queues when its main thread can service them. Install the matching
Python server and Unity package when deploying this protocol.

## Client configuration

- Claude Code: each UnityMCP registration has `timeout: 600000` milliseconds;
  `~/.claude/settings.json` has
  `env.CLAUDE_CODE_MCP_AUTO_BACKGROUND_MS: "0"`.
- Codex: global and existing project UnityMCP sections use
  `tool_timeout_sec = 600`.
- Antigravity: UnityMCP entries use `timeoutSeconds: 600`.
- hysp's `ruby/ishinoura_project_settings.rb` generates the same project timeouts
  and Antigravity global entries so later deployment preserves these values.

Official references: [Claude environment variables](https://code.claude.com/docs/en/env-vars),
[Codex configuration](https://developers.openai.com/codex/config-reference/),
[Antigravity MCP](https://antigravity.google/docs/mcp).

## Deployment and validation

Follow the 2026-11-12 through 2026-11-14 deployment record: commit/push this fork's
main, pull hysp, update `ruby/modify_manifest_json.rb` and its package manifest,
commit/push hysp, update all present target workspace project manifests, then use
UnityMCP `manage_packages` to resolve the pinned package in each running Editor.
Verify the running server revision, installed package source, completed compilation,
zero compile errors, and the live acceptance scenarios before reporting activation.

Unit tests cover admission, same-session attachment, client cancellation, busy
waiting and expiry, progress delivery, independent instances, durable cross-process
arbitration, journal reconciliation, and bypass for ordinary/recovery commands.
C# tests cover tombstones, queued cancellation, actual completion, reload disposal,
readiness and existing code-only scope. Live roomlariat checks additionally require
a connected UnityMCP server, two sessions, a side-effect counter and domain reload.

## Known execution boundary

The slot ends at synchronous handler return or the completion of an asynchronous
MCP handler. C# that registers `EditorApplication.update` callbacks and returns
releases the slot at that return; subsequent callbacks are outside this execution
manager. Managed callback jobs are outside this change.
