using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

// User secrets are only loaded in Development by default.
// Force-load them so `dotnet run -c Release` works.
builder.Configuration.AddUserSecrets<ExoSettings>();

builder.Services.Configure<ExoSettings>(builder.Configuration.GetSection("Exo"));
builder.Services.AddSingleton<HttpClient>();
builder.Services.AddHostedService<LeakReproWorker>();

await builder.Build().RunAsync();

// ---------------------------------------------------------------------------
// Settings — bound from "Exo" configuration section (user secrets / appsettings)
// ---------------------------------------------------------------------------
public sealed class ExoSettings
{
    public required string TenantId { get; init; }
    public required string ClientId { get; init; }
    public required string Username { get; init; }
    public required string Password { get; init; }
    public int Iterations { get; init; } = 500;
    public int PrintEvery { get; init; } = 10;
}

// ---------------------------------------------------------------------------
// Worker — connects to EXO, loops Get-EXOMailbox, keeps process alive for dumps
// ---------------------------------------------------------------------------
public sealed class LeakReproWorker(
    HttpClient httpClient,
    IOptions<ExoSettings> options,
    IHostApplicationLifetime lifetime
) : BackgroundService
{
    private readonly ExoSettings _settings = options.Value;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await RunAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — ignore.
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"\nFatal error: {ex}");
            Environment.ExitCode = 1;
            lifetime.StopApplication();
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        Console.WriteLine("Acquiring access token...");
        string token = await AcquireTokenAsync(ct);

        Console.WriteLine("Creating RunspacePool (1 runspace)...");
        var iss = InitialSessionState.CreateDefault();
        iss.ImportPSModule(["ExchangeOnlineManagement"]);
        iss.ThrowOnRunspaceOpenError = true;

        using var pool = RunspaceFactory.CreateRunspacePool(iss);
        pool.SetMinRunspaces(1);
        pool.SetMaxRunspaces(1);
        pool.ThreadOptions = PSThreadOptions.ReuseThread;
        pool.Open();

        Console.WriteLine($"Connecting to Exchange Online ({_settings.Username})...");
        Connect(pool, token);
        Console.WriteLine("Connected.\n");

        int pid = Environment.ProcessId;
        Console.WriteLine($"PID: {pid}");
        Console.WriteLine($"Running {_settings.Iterations:N0} Get-EXOMailbox invocations.");
        Console.WriteLine(
            "Each invocation creates a new AdminApiProvider.Container that is never disposed.\n"
        );

        PrintHeader();
        PrintMemory(0);

        for (int i = 1; i <= _settings.Iterations; i++)
        {
            ct.ThrowIfCancellationRequested();

            using var ps = PowerShell.Create();
            ps.RunspacePool = pool;
            ps.AddCommand("Get-EXOMailbox").AddParameter("ResultSize", 1);

            // PSDataCollection is properly materialized and disposed.
            // The leak is NOT here — it is inside the EXO module.
            var asyncResult = ps.BeginInvoke();
            using PSDataCollection<PSObject> results = ps.EndInvoke(asyncResult);
            _ = results.Count;

            if (ps.HadErrors && i <= 3)
            {
                foreach (var err in ps.Streams.Error)
                    Console.Error.WriteLine($"  [!] {err}");
            }

            if (i % _settings.PrintEvery == 0)
                PrintMemory(i);
        }

        Console.WriteLine();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        Console.WriteLine("After forced full GC + compaction:");
        PrintMemory(_settings.Iterations);

        Console.WriteLine(
            $"""

            === Repro complete ===

            Memory has grown linearly despite proper PSDataCollection disposal
            and a forced full GC. The retained objects are
            AdminApiProvider.Container instances that are never disposed.

            The process is kept alive for diagnostic tools:

              dotnet-dump collect -p {pid}
              dotnet-gcdump collect -p {pid}

            Suggested analysis:

              dotnet-dump analyze <dump-file>
              > dumpheap -type AdminApiProvider.Container -stat
              > dumpheap -type AdminApiProvider.Container
              > gcroot <address-of-any-container>

            Press Ctrl+C to exit.
            """
        );

        // Keep process alive for diagnostics.
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException) { }
    }

    private void Connect(RunspacePool pool, string token)
    {
        using var ps = PowerShell.Create();
        ps.RunspacePool = pool;
        ps.AddCommand("Connect-ExchangeOnline")
            .AddParameter("AccessToken", token)
            .AddParameter("UserPrincipalName", _settings.Username)
            .AddParameter(
                "CommandName",
                new[] { "Get-Mailbox", "Get-User", "Get-Recipient", "Get-DistributionGroup" }
            )
            .AddParameter("ShowBanner", false)
            .AddParameter("SkipLoadingFormatData", true);

        ps.Invoke();

        if (ps.HadErrors)
        {
            var errors = string.Join("\n", ps.Streams.Error.Select(e => e.ToString()));
            throw new InvalidOperationException($"Connect-ExchangeOnline failed:\n{errors}");
        }
    }

    private async Task<string> AcquireTokenAsync(CancellationToken ct)
    {
        using var response = await httpClient.PostAsync(
            $"https://login.microsoftonline.com/{_settings.TenantId}/oauth2/v2.0/token",
            new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["client_id"] = _settings.ClientId,
                    ["scope"] = "https://outlook.office365.com/.default",
                    ["grant_type"] = "password",
                    ["username"] = _settings.Username,
                    ["password"] = _settings.Password,
                }
            ),
            ct
        );

        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(ct),
            cancellationToken: ct
        );
        return json.RootElement.GetProperty("access_token").GetString()!;
    }

    private static void PrintHeader()
    {
        Console.WriteLine($"  {"Invocation", -12}  {"WorkingSet", 12}  {"Heap", 12}  {"Gen2", 6}");
        Console.WriteLine($"  {"----------", -12}  {"----------", 12}  {"----", 12}  {"----", 6}");
    }

    private static void PrintMemory(int iteration)
    {
        using var proc = Process.GetCurrentProcess();
        long heap = GC.GetTotalMemory(false);
        Console.WriteLine(
            $"  {iteration, -12:N0}  {proc.WorkingSet64 / 1024 / 1024, 9} MB  {heap / 1024 / 1024, 9} MB  {GC.CollectionCount(2), 6}"
        );
    }
}
