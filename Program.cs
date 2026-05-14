using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddUserSecrets<Program>(optional: true);
builder.Services.AddSingleton<SecretReporter>();

using var host = builder.Build();
host.Services.GetRequiredService<SecretReporter>().Run();

internal sealed class SecretReporter(IConfiguration configuration)
{
    public void Run()
    {
        var secretValue = configuration["Sample:SecretValue"];

        Console.WriteLine(secretValue is null
            ? "Sample:SecretValue is not set. Use 'dotnet user-secrets set \"Sample:SecretValue\" \"value\"'."
            : $"Loaded secret value: {secretValue}");
    }
}
