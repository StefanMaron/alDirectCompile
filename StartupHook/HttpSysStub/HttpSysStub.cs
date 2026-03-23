// Stub for Microsoft.AspNetCore.Server.HttpSys — redirects to Kestrel on Linux.
using System;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Microsoft.AspNetCore.Server.HttpSys
{
    public class HttpSysOptions
    {
        public bool AllowSynchronousIO { get; set; }
        public long? MaxConnections { get; set; }
        public int MaxAccepts { get; set; }
        public long? MaxRequestBodySize { get; set; }
        public AuthenticationManager Authentication { get; } = new AuthenticationManager();
        public TimeoutManager Timeouts { get; } = new TimeoutManager();
        public bool ThrowWriteExceptions { get; set; }
        public int RequestQueueLimit { get; set; }
        public string? RequestQueueName { get; set; }
        public bool UnsafePreferInlineScheduling { get; set; }
    }

    public class AuthenticationManager
    {
        public bool AllowAnonymous { get; set; } = true;
        public AuthenticationSchemes Schemes { get; set; } = AuthenticationSchemes.None;
        public bool AutomaticAuthentication { get; set; }
    }

    public class TimeoutManager
    {
        public TimeSpan IdleConnection { get; set; }
        public TimeSpan EntityBody { get; set; }
        public TimeSpan DrainEntityBody { get; set; }
        public TimeSpan RequestQueue { get; set; }
        public TimeSpan HeaderWait { get; set; }
        public long MinSendBytesPerSecond { get; set; }
    }

    [Flags]
    public enum AuthenticationSchemes
    {
        None = 0, Basic = 1, NTLM = 4, Negotiate = 8, Kerberos = 16,
    }
}

namespace Microsoft.AspNetCore.Hosting
{
    public static class WebHostBuilderHttpSysExtensions
    {
        private static int _portCounter = 0;

        public static IWebHostBuilder UseHttpSys(this IWebHostBuilder builder,
            Action<Microsoft.AspNetCore.Server.HttpSys.HttpSysOptions> configure)
        {
            // Assign sequential ports starting from 18000 to avoid conflicts
            int port = 18000 + System.Threading.Interlocked.Increment(ref _portCounter);
            Console.WriteLine($"[HttpSysStub] UseHttpSys redirected to UseKestrel (port {port})");

            var opts = new Microsoft.AspNetCore.Server.HttpSys.HttpSysOptions();
            configure?.Invoke(opts);

            return builder
                .UseKestrel(k =>
                {
                    k.AllowSynchronousIO = opts.AllowSynchronousIO;
                    if (opts.MaxRequestBodySize.HasValue)
                        k.Limits.MaxRequestBodySize = opts.MaxRequestBodySize;
                    // Bind to a specific port (avoids path-base issues from UseUrls)
                    k.ListenAnyIP(port);
                })
                // Don't use URLs from UseUrls (they have path bases Kestrel can't handle)
                .UseSetting(WebHostDefaults.PreferHostingUrlsKey, "false");
        }

        public static IWebHostBuilder UseHttpSys(this IWebHostBuilder builder)
        {
            Console.WriteLine("[HttpSysStub] UseHttpSys redirected to UseKestrel");
            return builder.UseKestrel();
        }
    }
}
