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
`SessionState`. A successful compilation remains pending until a delayed callback
after assembly reload. A callback for an older generation cannot clear a newer
request. Failed compilation reports `compilation_failed` until a later successful
compilation and reload; it does not permit code to run against stale assemblies.
A request that Unity defers or never starts remains blocked, rather than being
declared safe merely because a timeout expired.

`mcpforunity://editor/state` includes `compilation.is_compile_pending`,
`compilation.has_compile_errors`, and `execution.is_running`, with the active
execution ID and start time when available. Its readiness advice includes these
conditions and `assets.is_updating`.

## Execution IDs and exclusive admission

The shared Editor dispatcher admits one mutation at a time across HTTP, stdio,
clients, batches, and custom tools. Unknown commands acquire this same lease.
Diagnostic exceptions are ping, editor state, test-job status, console reads,
code history, and execution status. Console clearing acquires the mutation lease.

The Python transport supplies an `execution_id` when omitted. `execute_code`
also accepts an explicit stable ID for an intended execution or replay. A repeated
ID with identical command/arguments returns its recorded state or cached result.
Changed arguments produce `execution_id_conflict`. Every mutation receives an ID,
and execution status can list recent IDs for tools whose schema omits that input.

State is stored in `Library/MCPForUnity/command-executions.json`. Admission is
persisted before dispatch. The journal survives domain reload and transport/server
reconnect. Full results are retained for the latest 64 eligible completions, with
a 256 Ki-character limit per result. Older IDs and fingerprints remain recorded;
queries report `execution_result_not_retained` when the response was evicted.
Journal read/write failures block new mutations with `execution_storage_error`.

## Timeouts, status, and recovery

The response wait and actual handler completion have separate completion sources.
A timeout before handler start records `not_started` and cancels the queued work.
A timeout after handler start records `unknown`, holds the mutation lease, and
rejects further changes including new execution IDs. A late actual completion
stores the result and releases the lease. Both Python transports disable automatic
mutation resends and return `hint: inspect_execution` for uncertain outcomes.

Query `execute_code(action="get_execution_status", execution_id="...")` to inspect
one execution. Omit the ID to obtain the block state and the latest 20 execution
summaries. These queries read the journal on the transport thread and remain
available while the Unity main thread is blocked.
The HTTP receive loop continues accepting messages while an execution waits for
its result. Mutation admission stays exclusive, and response writes are serialized.

An unanswered diagnostic request latches `editor_unresponsive`. New mutation
admission also checks a 30-second main-thread heartbeat limit. Known pending
compilation is reported as `editor_busy` without latching this health fault.
A confirmed late completion releases a block belonging to that active execution.
A standalone health fault requires an Editor process restart. Domain reload,
elapsed time, a resumed heartbeat, and a new client connection preserve the block.
On a new Editor process, outstanding records become `interrupted`; their IDs
remain reserved and their earlier side effects remain part of the project state.

The execution boundary is the synchronous handler return or the task completion
reported by an asynchronous MCP handler. Arbitrary callbacks scheduled by user C#
after returning have a separate lifetime. Managed callback jobs and automatic
test splitting are outside this change's scope.
