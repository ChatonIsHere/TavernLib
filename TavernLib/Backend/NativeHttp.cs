using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TavernLib.Backend;

/// <summary>
/// HTTP(S) GETs through the operating system's own WinHTTP instead of Mono's
/// HttpClient. The mod manager fetches from OnEarlyInitializeMelon, before Unity
/// has registered its TLS provider with Mono, so at that point HttpClient falls
/// back to Mono's legacy TLS stack, which can't complete a handshake with GitHub:
/// every repo fetch and download failed with SecureChannelFailure. WinHTTP uses
/// the system's TLS on Windows and the host's TLS and CA store under Wine, so it
/// works from the first moment and doesn't care what state Mono's TLS is in.
///
/// It also honours the system proxy itself (automatic proxy, PAC included), the
/// job <see cref="WindowsProxy"/> does by hand for the HttpClient calls that
/// remain.
/// </summary>
public static class NativeHttp
{
    private const string UserAgent = "TavernLib";

    /// <summary>GETs url and returns the body as UTF-8 text. Throws on a non-2xx
    /// status or when timeout passes.</summary>
    public static string GetString(string url, TimeSpan timeout)
    {
        using var body = new MemoryStream();
        Get(url, DateTime.UtcNow + timeout, timeout, body);
        return Encoding.UTF8.GetString(body.ToArray());
    }

    /// <summary>Streams url's body into destination. Any single step - connecting,
    /// waiting for the response, each read - may take at most stallTimeout, and
    /// the whole transfer has to finish by deadlineUtc. Throws on a non-2xx status.</summary>
    public static void Download(string url, Stream destination, DateTime deadlineUtc, TimeSpan stallTimeout) =>
        Get(url, deadlineUtc, stallTimeout, destination);

    private static void Get(string url, DateTime deadlineUtc, TimeSpan stallTimeout, Stream destination)
    {
        var uri = new Uri(url);
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            throw new IOException($"Unsupported URL scheme '{uri.Scheme}'.");

        var session = OpenSession();
        var connection = IntPtr.Zero;
        var request = IntPtr.Zero;
        try
        {
            var timeoutMs = (int)Math.Min(RemainingMs(deadlineUtc, url), stallTimeout.TotalMilliseconds);
            Check(WinHttpSetTimeouts(session, timeoutMs, timeoutMs, timeoutMs, timeoutMs), "WinHttpSetTimeouts");

            connection = WinHttpConnect(session, uri.Host, (ushort)uri.Port, 0);
            Check(connection != IntPtr.Zero, "WinHttpConnect");

            var flags = uri.Scheme == Uri.UriSchemeHttps ? WinHttpFlagSecure : 0u;
            request = WinHttpOpenRequest(connection, "GET", uri.PathAndQuery, null, null, IntPtr.Zero, flags);
            Check(request != IntPtr.Zero, "WinHttpOpenRequest");

            // Redirects are followed by WinHTTP itself (GitHub release assets
            // redirect to their CDN), HTTPS-to-HTTP excepted.
            Check(WinHttpSendRequest(request, IntPtr.Zero, 0, IntPtr.Zero, 0, 0, IntPtr.Zero), "WinHttpSendRequest");
            Check(WinHttpReceiveResponse(request, IntPtr.Zero), "WinHttpReceiveResponse");

            uint status = 0, size = sizeof(uint);
            Check(WinHttpQueryHeaders(request, WinHttpQueryStatusCode | WinHttpQueryFlagNumber,
                IntPtr.Zero, ref status, ref size, IntPtr.Zero), "WinHttpQueryHeaders");
            if (status < 200 || status > 299)
                throw new IOException($"HTTP {status} from {uri.Host}");

            var buffer = new byte[1 << 16];
            while (true)
            {
                RemainingMs(deadlineUtc, url);
                Check(WinHttpReadData(request, buffer, (uint)buffer.Length, out var read), "WinHttpReadData");
                if (read == 0) break;
                destination.Write(buffer, 0, (int)read);
            }
        }
        finally
        {
            if (request != IntPtr.Zero) WinHttpCloseHandle(request);
            if (connection != IntPtr.Zero) WinHttpCloseHandle(connection);
            WinHttpCloseHandle(session);
        }
    }

    private static IntPtr OpenSession()
    {
        // Automatic proxy (Windows 8.1+) reads the same settings as the rest of
        // the system, PAC scripts included; anything older takes the
        // WinHTTP-configured proxy instead.
        var session = WinHttpOpen(UserAgent, WinHttpAccessTypeAutomaticProxy, null, null, 0);
        if (session == IntPtr.Zero)
            session = WinHttpOpen(UserAgent, WinHttpAccessTypeDefaultProxy, null, null, 0);
        Check(session != IntPtr.Zero, "WinHttpOpen");
        return session;
    }

    private static int RemainingMs(DateTime deadlineUtc, string url)
    {
        var left = (deadlineUtc - DateTime.UtcNow).TotalMilliseconds;
        if (left <= 0) throw new TimeoutException($"{url} took longer than its time budget.");
        return (int)Math.Min(left, int.MaxValue);
    }

    private static void Check(bool ok, string call)
    {
        if (ok) return;
        var code = Marshal.GetLastWin32Error();
        throw new IOException($"{call} failed: {Describe(code)}");
    }

    private static string Describe(int code) => code switch
    {
        12002 => "timed out (12002)",
        12007 => "the server name couldn't be resolved (12007)",
        12029 => "couldn't connect to the server (12029)",
        12030 => "the connection was dropped (12030)",
        12175 => "the secure connection failed - a TLS or certificate problem (12175)",
        _ => $"{new Win32Exception(code).Message} ({code})",
    };

    private const uint WinHttpAccessTypeDefaultProxy = 0;
    private const uint WinHttpAccessTypeAutomaticProxy = 4;
    private const uint WinHttpFlagSecure = 0x00800000;
    private const uint WinHttpQueryStatusCode = 19;
    private const uint WinHttpQueryFlagNumber = 0x20000000;

    [DllImport("winhttp.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr WinHttpOpen(string agent, uint accessType, string proxyName, string proxyBypass, uint flags);

    [DllImport("winhttp.dll", SetLastError = true)]
    private static extern bool WinHttpSetTimeouts(IntPtr handle, int resolveMs, int connectMs, int sendMs, int receiveMs);

    [DllImport("winhttp.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr WinHttpConnect(IntPtr session, string server, ushort port, uint reserved);

    [DllImport("winhttp.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr WinHttpOpenRequest(IntPtr connection, string verb, string objectName, string version,
        string referrer, IntPtr acceptTypes, uint flags);

    [DllImport("winhttp.dll", SetLastError = true)]
    private static extern bool WinHttpSendRequest(IntPtr request, IntPtr headers, uint headersLength, IntPtr optional,
        uint optionalLength, uint totalLength, IntPtr context);

    [DllImport("winhttp.dll", SetLastError = true)]
    private static extern bool WinHttpReceiveResponse(IntPtr request, IntPtr reserved);

    [DllImport("winhttp.dll", SetLastError = true)]
    private static extern bool WinHttpQueryHeaders(IntPtr request, uint infoLevel, IntPtr name, ref uint buffer,
        ref uint bufferLength, IntPtr index);

    [DllImport("winhttp.dll", SetLastError = true)]
    private static extern bool WinHttpReadData(IntPtr request, byte[] buffer, uint bytesToRead, out uint bytesRead);

    [DllImport("winhttp.dll", SetLastError = true)]
    private static extern bool WinHttpCloseHandle(IntPtr handle);
}
