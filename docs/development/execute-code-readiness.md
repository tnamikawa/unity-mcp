# Execute code readiness

`execute_code` checks the live Editor state before compiling a snippet and again
before invoking it. The same handler covers `replay` and calls from `batch_execute`.
The check is independent of `safety_checks`; disabling the string-pattern filter
does not disable readiness checks.

Execution is rejected with `editor_busy` and a `data.reason` while scripts are
compiling, a compilation request or assembly reload is pending, assets are being
updated, or the Editor is entering or leaving Play Mode. Stable Play Mode remains
supported. Calls made outside the Unity main thread are rejected too.

Compilation requests made by `refresh_unity` and script-editing tools are marked
pending **before** requesting compilation. The marker survives domain reload via
`SessionState`. The `afterAssemblyReload` notification clears the generation loaded
into that domain directly. This release is independent of Inspector updates and
`EditorApplication.delayCall`. Requests issued during initialization or another
reload callback retain their newer generation. A rejected compilation request
restores the previous generation if no newer request or compilation has started.
Failed compilation reports `compilation_failed` until a later successful
compilation and reload; it does not permit code to run against stale assemblies.
A deferred compilation request keeps C# execution pending until compilation and
assembly reload complete.

`mcpforunity://editor/state` includes `compilation.is_compile_pending`,
`compilation.has_compile_errors`, and `execution.is_running`, with the active
execution ID and start time when available. Its `advice.ready_for_code_execution` and
`advice.code_execution_blocking_reasons` report these conditions and
`assets.is_updating`. These additional gates affect code-specific advice; ordinary
operations keep their existing readiness advice and handler behavior.

## Execution IDs and exclusive admission

The shared Editor dispatcher admits one C# execution at a time across HTTP,
stdio, and clients. The scope is `execute_code(action="execute"|"replay")`, plus
`batch_execute` and `execute_custom_tool` calls containing those actions. Nested
batches are inspected using the effective parameter keys produced by Unity's
batch normalization. Ordinary commands and code-free wrappers pass through this
policy, including Play stop/start, scene queries, asset searches, refresh requests,
console clearing, and code history/status/clearing.

For calls in this scope, the Python transport supplies an `execution_id` when
omitted. `execute_code` also accepts an explicit stable ID for an intended
execution or replay. A repeated ID with identical command/arguments returns its
recorded state or cached result. Changed arguments produce `execution_id_conflict`.

State is stored in `Library/MCPForUnity/command-executions.json`. Code admission is
persisted before dispatch. The journal survives domain reload and transport/server
reconnect. Full results are retained for the latest 64 eligible completions, with
a 256 Ki-character limit per result. Older code execution IDs and fingerprints
remain recorded to prevent duplicate execution; queries report
`execution_result_not_retained` when the response was evicted. Journal failures
block C# execution with `execution_storage_error`. Ordinary commands receive no
automatic execution ID, lease, or journal writes and retain their transport retry
behavior, including each handler's explicit retry settings.

On loading an older journal, ordinary command records and the former diagnostic
`editor_unresponsive` fault are removed. Legacy batch/custom-wrapper records retain
their execution identity because their original arguments were not stored.
A current-format journal with unchanged state is loaded without a rewrite.

## Timeouts, status, and recovery

Code-bearing requests use separate completion sources for the response wait and
actual handler completion. Ordinary requests retain the original cancellation and
next-Editor-frame cleanup behavior. A code timeout before handler start records `not_started`
and cancels the queued work. A timeout after handler start records `unknown`, holds
the code execution lease, and rejects further code-bearing calls. A late actual
completion stores the result and releases the lease. Both Python transports
disable automatic resends after a code-bearing request has entered the send phase
and return `hint: inspect_execution` for uncertain outcomes. Connection/reload waits
before the first send retain their existing budgets and explicit caller settings.
An unavailable connection before sending reports `state: not_sent` with a retry hint.

Query `execute_code(action="get_execution_status", execution_id="...")` to inspect
one execution. Omit the ID to obtain `code_execution_blocked` and the latest 20
execution summaries. These queries read the journal on the transport thread and
remain available while the Unity main thread is blocked, once the transport has
finished the preceding response wait. WebSocket messages retain sequential handling.
Stdio keeps its original ping response, command queue, timeout budget, and broker
resend deduplication for ordinary requests. Only code-bearing calls and execution
status queries use the shared dispatcher from the stdio receive path.

An unanswered diagnostic request returns its timeout/cancellation result. Idle
time and diagnostic response delays leave the code execution lease unchanged.
Recovery commands remain eligible for dispatch during compilation and an unknown
code outcome; Unity handles them when its main thread can process the queue.
The refresh completion wait retains its original compilation, asset-update, test,
and Play Mode conditions.
On a new Editor process, outstanding code records become `interrupted`; their IDs
remain reserved and their earlier side effects remain part of the project state.

The execution boundary is the synchronous handler return or the task completion
reported by an asynchronous MCP handler. Arbitrary callbacks scheduled by user C#
after returning have a separate lifetime. Managed callback jobs and automatic
test splitting are outside this change's scope.
