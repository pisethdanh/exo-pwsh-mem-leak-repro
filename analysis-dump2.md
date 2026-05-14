# Get-EXOMailbox Dump 2 — Mid-Execution with -CommandName (252 invocations)

## Environment

- .NET 10, Ubuntu Linux (glibc), no GC hard limits
- ExchangeOnlineManagement 3.9.2, Microsoft.PowerShell.SDK 7.6.1
- Cmdlet: `Get-EXOMailbox -ResultSize 1` (REST API cmdlet)
- `Connect-ExchangeOnline` called with
  `-CommandName Get-Mailbox,Get-User,Get-Recipient,Get-DistributionGroup`
- Dump taken mid-execution (~252 of 500 invocations completed)

## Container count

```text
> dumpheap -type AdminApiProvider.Container -stat

          MT Count TotalSize Class Name
7d0ebac28b68   252   171,360 Microsoft.Exchange.Management.AdminApiProvider.Container
```

**252 containers for ~252 invocations** — exact 1:1 match. Zero collected by
GC despite earlier invocations having completed long before the dump.

## GC root analysis

Rooted: `7d08d29d5fc0` (a mid-list container — NOT the most recent invocation)

```text
> gcroot 7d08d29d5fc0
Found 8 unique roots.
```

### Root chain — static ConsoleCancelEventHandler leak

All 8 roots converge on the same chain:

```text
HandleTable (strong handle):
    00007d0f332213e8
      → System.Object[] (static delegate invocation list)
        → ConsoleCancelEventHandler
          (static variable: System.Text.Encoding.s_outputEncoding)
          → System.Object[] (multicast delegate array)
            → ConsoleCancelEventHandler
              → Microsoft.Exchange.Management.RestApiClient.GetExoMailbox
                → Microsoft.Exchange.Management.AdminApiProvider.Container
```

The remaining 7 roots are thread stack references (`WorkerThread`,
`GateThread`, `Task.SpinThenBlockingWait`) that all resolve through the same
static `Object[]` → `ConsoleCancelEventHandler` chain.

### What this means

1. The root cause is identical to Dump 1: each `GetExoMailbox` cmdlet
   registers a `ConsoleCancelEventHandler` on the
   static `Console.CancelKeyPress` event and never unsubscribes.

2. No `DynamicResolver` or `RuntimeTypeCache` roots appear for the
   `AdminApiProvider.Container` objects. The proxy compilation creates GC
   handles for the proxy infrastructure, but those handles do not root the
   containers created by `Get-EXOMailbox`.

3. The `ConsoleCancelEventHandler` is the sole retention mechanism for `Get-EXOMailbox` containers.

### Comparison to previous dumps

| Dump | Containers | -CommandName | Root type                 | Status    |
| ---: | ---------: | :----------- | :------------------------ | :-------- |
|    1 |        500 | No           | ConsoleCancelEventHandler | Post-exec |
|    2 |        252 | Yes          | ConsoleCancelEventHandler | Mid-exec  |

Both dumps confirm the same leak: one `AdminApiProvider.Container` per
`Get-EXOMailbox` invocation, never collected.

### Memory impact

| Invocations | Containers | Total size (containers only) |
| ----------: | ---------: | ---------------------------: |
|         252 |        252 |                       171 KB |

Each container also retains its DI graph (`ApiProvider`, `AsyncConsoleLogger`,
`CmdletIOPipeline`, `OData` client objects, etc.), so actual retained memory
per invocation is significantly larger than the 680 bytes per Container object.
