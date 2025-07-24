using SelfcareService;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<Worker>();

// Add support for Windows Service and Linux systemd
if (OperatingSystem.IsWindows())
{
    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "SelfcareService";
    });
}
else if (OperatingSystem.IsLinux())
{
    builder.Services.AddSystemd();
}

var host = builder.Build();
host.Run();
