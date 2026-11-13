using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Browser;
using Raf.Client;
using Raf.Client.Browser;
using Raf.Client.Services;

[assembly: SupportedOSPlatform("browser")]

ClientHost.Current = new BrowserHost();
await AppBuilder.Configure<App>().WithInterFont().StartBrowserAppAsync("out");
