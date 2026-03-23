// Stub for Microsoft.AspNetCore.Server.HttpSys — redirects to Kestrel on Linux.
// BC calls: builder.UseHttpSys(configure) then builder.UseUrls("http://+:7048/BC/api")
// We redirect to Kestrel and use an IStartupFilter to strip URL paths at app startup.
using System;
using System.Linq;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
        None = 0, Basic = 1, Anonymous = 2, NTLM = 4, Negotiate = 8, Kerberos = 16,
    }
}

namespace Microsoft.AspNetCore.Hosting
{
    public static class WebHostBuilderHttpSysExtensions
    {
        private static readonly System.Collections.Generic.HashSet<int> _boundPorts = new();

        public static IWebHostBuilder UseHttpSys(this IWebHostBuilder builder,
            Action<Microsoft.AspNetCore.Server.HttpSys.HttpSysOptions> configure)
        {
            var opts = new Microsoft.AspNetCore.Server.HttpSys.HttpSysOptions();
            configure?.Invoke(opts);

            builder.UseKestrel(k =>
            {
                k.AllowSynchronousIO = opts.AllowSynchronousIO;
                if (opts.MaxRequestBodySize.HasValue)
                    k.Limits.MaxRequestBodySize = opts.MaxRequestBodySize;
            });

            // Register a startup filter that strips URL paths and deduplicates ports.
            // IStartupFilter.Configure runs AFTER all builder config is done but BEFORE
            // the server starts listening — the right time to modify URLs.
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IStartupFilter>(new UrlStrippingStartupFilter(_boundPorts));

                // Register passthrough auth. BC may also register BasicAuthentication/Negotiate
                // which fail on Linux. We set Passthrough as default so it runs first.
                services.AddAuthentication("Passthrough")
                    .AddScheme<AuthenticationSchemeOptions, PassthroughAuthHandler>("Passthrough", o => { });
                // Force Passthrough as default for ALL auth/authz operations
                services.PostConfigureAll<AuthenticationOptions>(authOpts =>
                {
                    authOpts.DefaultScheme = "Passthrough";
                    authOpts.DefaultChallengeScheme = "Passthrough";
                    authOpts.DefaultAuthenticateScheme = "Passthrough";
                    authOpts.DefaultForbidScheme = "Passthrough";
                    authOpts.DefaultSignInScheme = "Passthrough";
                    authOpts.DefaultSignOutScheme = "Passthrough";
                });
                services.PostConfigureAll<Microsoft.AspNetCore.Authorization.AuthorizationOptions>(authzOpts =>
                {
                    var policy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder("Passthrough")
                        .RequireAuthenticatedUser()
                        .Build();
                    authzOpts.DefaultPolicy = policy;
                    authzOpts.FallbackPolicy = null;
                    authzOpts.AddPolicy("AdminService", policy);
                });
            });

            return builder;
        }

        public static IWebHostBuilder UseHttpSys(this IWebHostBuilder builder)
        {
            return builder.UseKestrel();
        }
    }

    internal class UrlStrippingStartupFilter : IStartupFilter
    {
        private readonly System.Collections.Generic.HashSet<int> _boundPorts;

        public UrlStrippingStartupFilter(System.Collections.Generic.HashSet<int> boundPorts)
        {
            _boundPorts = boundPorts;
        }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                // Strip paths from server addresses before the server starts
                var addressFeature = app.ServerFeatures.Get<IServerAddressesFeature>();
                string? pathBase = null;
                if (addressFeature != null && addressFeature.Addresses.Count > 0)
                {
                    var original = addressFeature.Addresses.ToList();
                    addressFeature.Addresses.Clear();
                    foreach (var addr in original)
                    {
                        var (stripped, path) = StripPath(addr);
                        if (stripped != null)
                        {
                            addressFeature.Addresses.Add(stripped);
                            if (!string.IsNullOrEmpty(path))
                                pathBase = path;
                            Console.WriteLine($"[HttpSysStub] {addr} → {stripped}");
                        }
                        else
                        {
                            Console.WriteLine($"[HttpSysStub] {addr} → skipped (port in use)");
                        }
                    }
                }

                // Add path base so BC's middleware routes correctly
                // e.g., requests to /BC/dev/packages get PathBase=/BC/dev, Path=/packages
                if (!string.IsNullOrEmpty(pathBase))
                {
                    Console.WriteLine($"[HttpSysStub] UsePathBase({pathBase})");
                    app.UsePathBase(pathBase);
                }

                next(app);
            };
        }

        private (string? address, string? pathBase) StripPath(string url)
        {
            try
            {
                var parsed = url.Replace("://+:", "://localhost:").Replace("://*:", "://localhost:");
                var uri = new Uri(parsed);
                var scheme = url.Substring(0, url.IndexOf("://"));
                var host = url.Contains("://+:") ? "+" : url.Contains("://*:") ? "*" : uri.Host;
                var port = uri.Port;
                var path = uri.AbsolutePath.TrimEnd('/');

                lock (_boundPorts)
                {
                    if (!_boundPorts.Add(port))
                    {
                        // Port already bound — find next available
                        while (!_boundPorts.Add(++port)) { }
                    }
                }
                return ($"{scheme}://{host}:{port}", string.IsNullOrEmpty(path) || path == "/" ? null : path);
            }
            catch
            {
                return (url, null);
            }
        }
    }

    /// <summary>
    /// Authentication handler that always succeeds with a SYSTEM identity.
    /// Used when BC doesn't configure any auth scheme (e.g., management API on Linux).
    /// </summary>
    internal class PassthroughAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public PassthroughAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger, UrlEncoder encoder) : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new[] {
                new Claim(ClaimTypes.Name, "admin"),
                new Claim(ClaimTypes.Role, "SUPER"),
                new Claim(ClaimTypes.NameIdentifier, "00000000-0000-0000-0000-000000000001"),
            };
            var identity = new ClaimsIdentity(claims, "Passthrough");
            var principal = new ClaimsPrincipal(identity);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "Passthrough")));
        }
    }
}
