# ExchangeOnlineManagement Memory Leak Repro

Minimal reproduction of an `AdminApiProvider.Container` memory leak in the
[ExchangeOnlineManagement](https://www.powershellgallery.com/packages/ExchangeOnlineManagement)
PowerShell module when hosted in a .NET [RunspacePool](https://github.com/PowerShell/PowerShell/blob/90d3b7f2e355e457d92b6929f6b4cfe4fa651e35/src/System.Management.Automation/engine/hostifaces/RunspacePool.cs).

## Problem

The `ExchangeOnlineManagement` module creates a new
`Microsoft.Exchange.Management.AdminApiProvider.Container` per cmdlet
invocation. These containers are **never disposed** and are permanently pinned
by CLR GC Handles.

The specific GC root mechanism depends on the hosting context:

- **Console app** (this repro): `ConsoleCancelEventHandler` — each
  `Get-EXOMailbox` invocation registers a handler on the static
  `Console.CancelKeyPress` event and never unsubscribes
- **Non-console host** (web app, hosted service, k8s pod):
  `DynamicResolver` / `RuntimeTypeCache` — CLR JIT/reflection GC Handles
  from the module's internal DI/service resolution graph

The root mechanism varies, but the underlying bug is the same: one
`AdminApiProvider.Container` per invocation, never reused or disposed.

No in-process operation can free them:

- `Remove-Module` / `Import-Module` — does not release GC Handles
- `RunspacePool.Dispose()` — GC Handles survive disposal
- `Disconnect-ExchangeOnline` — closes HTTP sessions only
- Full GC with aggressive compaction — the handles are strong roots

**Only process exit releases the handles.**

### Impact

Memory grows linearly with each cmdlet invocation. Each invocation retains
an `AdminApiProvider.Container` (680 bytes) plus its entire DI object graph
(`ApiProvider`, `AsyncConsoleLogger`, `CmdletIOPipeline`, OData client
objects, etc.). The total retained size per invocation is significantly
larger than the 680-byte container object alone.

In our production environment (.NET 10, Alpine Linux, 2 Gi pod limit), this
causes `OutOfMemoryException` during large batch operations (100K+ cmdlet
invocations per pod lifetime).

### Where the bug is

Each `Get-EXOMailbox` invocation triggers the module's internal DI
`ServiceLookup` graph to create a new `AdminApiProvider.Container`. The
container is never reused or disposed.

In a **console app** (this repro), the container is rooted by a
`ConsoleCancelEventHandler` registered on the static `Console.CancelKeyPress`
event and never unsubscribed:

```text
Console.CancelKeyPress (static event)
  → ConsoleCancelEventHandler → GetExoMailbox → AdminApiProvider.Container
```

In a **non-console host** (web app, hosted service, k8s pod), the container
is rooted through the module's internal DI/service resolution graph, pinned
by CLR JIT/reflection GC Handles:

```text
AdminApiProvider.Container
  → RuntimeFile → RuntimeMethodInfo → ... → DynamicResolver → [ROOT:Handle]
  → RuntimeFile → RuntimeMethodInfo → ... → RuntimeTypeCache → [ROOT:Handle]
```

None of these roots are accessible via any public API.

### Suggested fix

The root cause is that `AdminApiProvider.Container` is created per cmdlet
invocation and never reused or disposed. The module should either:

1. **Reuse** a single `AdminApiProvider.Container` across invocations within
   the same connection session, or
2. **Dispose** containers after each invocation to break the reference chains
   that prevent GC collection

The containers and their DI graph are internal to the module — there is no
public API for consumers to dispose or control them.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [ExchangeOnlineManagement](https://www.powershellgallery.com/packages/ExchangeOnlineManagement)
  PowerShell module (v3.x)
- An Entra ID app registration with:
  - **Delegated permission**: `Exchange.Manage` (admin-consented)
  - **Allow public client flows** enabled (`az ad app update --id <app-id> --is-fallback-public-client true`)
- A user account with **Global Administrator** role in Entra ID
  - MFA must be disabled or exempted for this account (ROPC flow does not
    support interactive MFA prompts)

### Install diagnostic tools

```bash
dotnet tool install -g dotnet-dump
dotnet tool install -g dotnet-gcdump
```

### Install the EXO module

```powershell
Install-Module ExchangeOnlineManagement -Scope CurrentUser
```

## Setup

### 1. Clone and restore

```bash
git clone <repo-url>
cd exo-pwsh-mem-leak-repro
dotnet restore
```

### 2. Configure credentials via user secrets

Run the interactive helper:

```bash
# Bash (Linux / macOS)
./set-secrets.sh

# PowerShell (Windows)
./set-secrets.ps1
```

Or set each secret manually:

```bash
dotnet user-secrets set "Exo:TenantId" "<your-tenant-id>"
dotnet user-secrets set "Exo:ClientId" "<your-app-client-id>"
dotnet user-secrets set "Exo:Username" "admin@contoso.onmicrosoft.com"
dotnet user-secrets set "Exo:Password" "<user-password>"
```

Verify secrets are stored:

```bash
dotnet user-secrets list
```

### 3. (Optional) Adjust iteration count

Edit `appsettings.json` to change the number of invocations or the print
interval:

```json
{
  "Exo": {
    "Iterations": 500,
    "PrintEvery": 10
  }
}
```

## Run the repro

```bash
dotnet run -c Release
```

Expected output (with default 500 iterations):

```text
Acquiring access token...
Creating RunspacePool (1 runspace)...
Connecting to Exchange Online (admin@contoso.onmicrosoft.com)...
Connected.

PID: 12345
Running 500 Get-EXOMailbox invocations.
...

After forced full GC + compaction:
  <iteration>    <working-set>    <heap>    <gen2>

=== Repro complete ===

Memory has grown linearly despite proper PSDataCollection disposal
and a forced full GC.
```

The process stays alive after the loop for diagnostic tool collection.

## Collect a dump

In a second terminal while the process is waiting:

```bash
# Full heap dump (for dumpheap / gcroot analysis)
dotnet-dump collect -p <PID>

# Or lightweight GC dump (for type statistics only)
dotnet-gcdump collect -p <PID>
```

## Analyze

```bash
dotnet-dump analyze core_<timestamp>
```

### Verify Container count matches invocation count

```text
> dumpheap -type AdminApiProvider.Container -stat
```

The instance count should match the number of cmdlet invocations
(e.g., 5,000 containers for 5,000 invocations).

### Trace a Container to its GC root

```text
> dumpheap -type AdminApiProvider.Container
```

Pick any container address from the output:

```text
> gcroot <address>
```

The root chain depends on the hosting context:

- **Console app** (this repro): a static `ConsoleCancelEventHandler` delegate
  array — the cmdlet registers a Ctrl+C handler on `Console.CancelKeyPress`
  and never unsubscribes
- **Non-console host** (web app, k8s pod): `DynamicResolver` or
  `RuntimeTypeCache` GC Handles — CLR JIT/reflection infrastructure from
  the module's internal DI/service graph

## Module version tested

- ExchangeOnlineManagement **3.9.2**
- Microsoft.PowerShell.SDK **7.6.1**
- .NET **10.0**
- Tested on Linux (Alpine/musl) and Ubuntu; the leak is not
  platform-specific
