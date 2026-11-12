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

## Timeouts and retries

A transport timeout cancels the response wait, not synchronous C# execution.
An execution lease stays active until the handler actually returns, including
exception cleanup. Incoming transport requests during that execution are rejected
immediately with `execution_in_progress`, the execution ID, and
`execution_stopped: false`; they are not queued to run after the caller times out.
The Python tool preserves error text and structured readiness details.

Inspect readiness before retrying. There is no automatic replay of a timed-out
snippet. A response timeout does not prove that a snippet made no changes.

These checks govern entry into synchronous snippets. They cannot interrupt a
native Unity call already in progress or track arbitrary background work or
callbacks that a snippet schedules after returning.
