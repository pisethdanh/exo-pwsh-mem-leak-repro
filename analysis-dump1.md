# Get-EXOMailbox Dump 1 — Post-Completion (500 invocations)

## Environment

- .NET 10, Ubuntu Linux (glibc), no GC hard limits
- ExchangeOnlineManagement 3.9.2, Microsoft.PowerShell.SDK 7.6.1
- Cmdlet: `Get-EXOMailbox -ResultSize 1` (REST API cmdlet)
- `Connect-ExchangeOnline` called without `-CommandName`
- Dump taken after all 500 invocations completed (process kept alive)

## Container count

```text
> dumpheap -type AdminApiProvider.Container -stat

          MT Count TotalSize Class Name
7a4055e1f618   500   340,000 Microsoft.Exchange.Management.AdminApiProvider.Container
```

**500 containers for 500 invocations** — exact 1:1 match. Zero collected by GC
despite all invocations having completed.

## GC root analysis

Rooted: `7a3a727890d8` (a mid-list container — NOT the last invocation, this
cmdlet finished long before the dump)

```text
> gcroot 7a3a727890d8
Found 8 unique roots.
```

### Root chain — static ConsoleCancelEventHandler leak

All 8 roots converge on the same chain:

```text
HandleTable (strong handle)
  → System.Object[] (static delegate invocation list)
    → ConsoleCancelEventHandler
      → System.Object[] (multicast delegate array)
        → ConsoleCancelEventHandler
          → Microsoft.Exchange.Management.RestApiClient.GetExoMailbox
            → Microsoft.Exchange.Management.AdminApiProvider.Container
```

### What this means

1. Each `GetExoMailbox` cmdlet **registers a `ConsoleCancelEventHandler`**
   during `ProcessRecord()` — this is the module's Ctrl+C cancellation support.

2. `Console.CancelKeyPress` is a **static event**. Handlers are stored in a
   process-wide static delegate array.

3. The handler **captures a reference** to the `GetExoMailbox` instance, which
   holds the `AdminApiProvider.Container`.

4. The handler is **never unregistered** after the cmdlet completes
   (`EndProcessing()` / `StopProcessing()` do not remove it).

5. Therefore, every `GetExoMailbox` instance and its `Container` are
   **permanently rooted** for the lifetime of the process.

### Why this root is definitive

This container's cmdlet finished executing long before the dump
was taken. There are no active pipelines, no in-flight HTTP requests, no thread
stacks holding it. The **only** root is the leaked static event handler. This
proves the containers cannot be collected even after all work is done.

### Memory impact

| Invocations | Containers | Total size (containers only) |
| ----------: | ---------: | ---------------------------: |
|         500 |        500 |                       340 KB |

Each container also retains its DI graph (`ApiProvider`, `AsyncConsoleLogger`,
`CmdletIOPipeline`, `OData` client objects, etc.), so actual retained memory
per invocation is significantly larger than the 680 bytes per Container object.
