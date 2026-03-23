// Stub for Microsoft.AspNetCore.Server.HttpSys — redirects to Kestrel on Linux.
// BC sets URLs via UseUrls() on the original builder (not our return value),
// so we can't intercept URL configuration. Instead, we strip paths from
// ASPNETCORE_URLS at startup and bind Kestrel to ports extracted from
// the builder's settings at host build time.
using System;
using System.Linq;
using Microsoft.AspNetCore.Hosting;

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
        // Track which ports are already bound to avoid duplicates
        private static readonly System.Collections.Generic.HashSet<int> _boundPorts = new();

        public static IWebHostBuilder UseHttpSys(this IWebHostBuilder builder,
            Action<Microsoft.AspNetCore.Server.HttpSys.HttpSysOptions> configure)
        {
            var opts = new Microsoft.AspNetCore.Server.HttpSys.HttpSysOptions();
            configure?.Invoke(opts);

            // BC calls UseUrls("http://+:PORT/BC/path") on the original builder
            // (not on our return value). We configure Kestrel here and strip the
            // URL via ConfigureServices which runs after all builder config is done.
            builder.UseKestrel(k =>
            {
                k.AllowSynchronousIO = opts.AllowSynchronousIO;
                if (opts.MaxRequestBodySize.HasValue)
                    k.Limits.MaxRequestBodySize = opts.MaxRequestBodySize;
            });

            // Register a service that strips the URL path before Kestrel starts.
            // ConfigureServices runs after all builder configuration is complete.
            builder.ConfigureServices((context, services) =>
            {
                var urls = builder.GetSetting(WebHostDefaults.ServerUrlsKey);
                if (!string.IsNullOrEmpty(urls))
                {
                    var stripped = StripUrlPaths(urls);
                    builder.UseSetting(WebHostDefaults.ServerUrlsKey, stripped);
                    Console.WriteLine($"[HttpSysStub] UseHttpSys → Kestrel ({stripped})");
                }
                else
                {
                    Console.WriteLine("[HttpSysStub] UseHttpSys → Kestrel (no URL configured)");
                }
            });

            return builder;
        }

        public static IWebHostBuilder UseHttpSys(this IWebHostBuilder builder)
        {
            Console.WriteLine("[HttpSysStub] UseHttpSys redirected to UseKestrel");
            return builder.UseKestrel();
        }

        private static string StripUrlPaths(string urls)
        {
            var parts = urls.Split(';')
                .Select(url =>
                {
                    var trimmed = url.Trim();
                    if (string.IsNullOrEmpty(trimmed)) return null;
                    try
                    {
                        var parsed = trimmed
                            .Replace("://+:", "://localhost:")
                            .Replace("://*:", "://localhost:");
                        var uri = new Uri(parsed);
                        var scheme = trimmed.Substring(0, trimmed.IndexOf("://"));
                        var host = trimmed.Contains("://+:") ? "+" :
                                   trimmed.Contains("://*:") ? "*" : uri.Host;
                        var port = uri.Port;

                        // If this port is already bound by another host, assign next available
                        // (BC creates multiple hosts on the same port with different paths)
                        lock (_boundPorts)
                        {
                            while (!_boundPorts.Add(port))
                                port++;
                        }
                        return $"{scheme}://{host}:{port}";
                    }
                    catch { return trimmed; }
                })
                .Where(s => s != null);
            return string.Join(";", parts);
        }
    }
}
