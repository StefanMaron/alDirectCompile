// Stub for Microsoft.AspNetCore.Server.HttpSys — redirects to Kestrel on Linux.
// UseHttpSys() calls are silently replaced with UseKestrel().

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
        public HttpSysAuthentication Authentication { get; } = new HttpSysAuthentication();
    }

    public class HttpSysAuthentication
    {
        public bool AllowAnonymous { get; set; } = true;
        public AuthenticationSchemes Schemes { get; set; } = AuthenticationSchemes.None;
    }

    [System.Flags]
    public enum AuthenticationSchemes
    {
        None = 0,
        Basic = 1,
        NTLM = 4,
        Negotiate = 8,
        Kerberos = 16,
    }
}

namespace Microsoft.AspNetCore.Hosting
{
    public static class WebHostBuilderHttpSysExtensions
    {
        /// <summary>
        /// Replaces UseHttpSys with UseKestrel on Linux.
        /// The HttpSysOptions callback is accepted but only AllowSynchronousIO is applied.
        /// </summary>
        public static IWebHostBuilder UseHttpSys(this IWebHostBuilder builder,
            System.Action<Microsoft.AspNetCore.Server.HttpSys.HttpSysOptions> configure)
        {
            System.Console.WriteLine("[HttpSysStub] UseHttpSys redirected to UseKestrel");

            // Capture HttpSys options to apply compatible settings to Kestrel
            var httpSysOptions = new Microsoft.AspNetCore.Server.HttpSys.HttpSysOptions();
            configure?.Invoke(httpSysOptions);

            return builder.UseKestrel(kestrelOptions =>
            {
                kestrelOptions.AllowSynchronousIO = httpSysOptions.AllowSynchronousIO;
                if (httpSysOptions.MaxRequestBodySize.HasValue)
                    kestrelOptions.Limits.MaxRequestBodySize = httpSysOptions.MaxRequestBodySize;
            });
        }

        public static IWebHostBuilder UseHttpSys(this IWebHostBuilder builder)
        {
            System.Console.WriteLine("[HttpSysStub] UseHttpSys redirected to UseKestrel");
            return builder.UseKestrel();
        }
    }
}
