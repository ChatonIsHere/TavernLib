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
public sealed class NativeHttp : IDisposable
{
    private const string UserAgent = "TavernLib";
    private IntPtr _session;

    /// <summary>GETs url and returns the body as UTF-8 text. Throws on a non-2xx
    /// status, when timeout passes, or past maxBytes.</summary>
    public string GetString(string url, TimeSpan timeout, long maxBytes)
    {
        using var body = new MemoryStream();
        Get(url, DateTime.UtcNow + timeout, timeout, body, maxBytes);
        return Encoding.UTF8.GetString(body.ToArray());
    }

    /// <summary>Streams url's body into destination. Any single step - connecting,
    /// waiting for the response, each read - may take at most stallTimeout, and
    /// the whole transfer has to finish by deadlineUtc. Throws on a non-2xx status
    /// or past maxBytes.</summary>
    public void Download(string url, Stream destination, DateTime deadlineUtc, TimeSpan stallTimeout, long maxBytes) =>
        Get(url, deadlineUtc, stallTimeout, destination, maxBytes);

    public void Dispose()
    {
        if (_session == IntPtr.Zero) return;
        WinHttpCloseHandle(_session);
        _session = IntPtr.Zero;
    }

    private void Get(string url, DateTime deadlineUtc, TimeSpan stallTimeout, Stream destination, long maxBytes)
    {
        var uri = new Uri(url);
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            throw new IOException($"Unsupported URL scheme '{uri.Scheme}'.");

        var connection = IntPtr.Zero;
        var request = IntPtr.Zero;
        try
        {
            connection = WinHttpConnect(Session(), uri.Host, (ushort)uri.Port, 0);
            Check(connection != IntPtr.Zero, "WinHttpConnect");

            var flags = uri.Scheme == Uri.UriSchemeHttps ? WinHttpFlagSecure : 0u;
            request = WinHttpOpenRequest(connection, "GET", uri.PathAndQuery, null, null, IntPtr.Zero, flags);
            Check(request != IntPtr.Zero, "WinHttpOpenRequest");

            // On the request, not the shared session.
            var timeoutMs = (int)Math.Min(RemainingMs(deadlineUtc, url), stallTimeout.TotalMilliseconds);
            Check(WinHttpSetTimeouts(request, timeoutMs, timeoutMs, timeoutMs, timeoutMs), "WinHttpSetTimeouts");

            // Redirects are followed by WinHTTP itself (GitHub release assets
            // redirect to their CDN), HTTPS-to-HTTP excepted.
            Check(WinHttpSendRequest(request, IntPtr.Zero, 0, IntPtr.Zero, 0, 0, IntPtr.Zero), "WinHttpSendRequest");
            Check(WinHttpReceiveResponse(request, IntPtr.Zero), "WinHttpReceiveResponse");

            uint status = 0, size = sizeof(uint);
            Check(WinHttpQueryHeaders(request, WinHttpQueryStatusCode | WinHttpQueryFlagNumber,
                IntPtr.Zero, ref status, ref size, IntPtr.Zero), "WinHttpQueryHeaders");
            if (status < 200 || status > 299)
                throw new IOException($"HTTP {status} from {uri.Host}");

            uint length = 0;
            size = sizeof(uint);
            if (WinHttpQueryHeaders(request, WinHttpQueryContentLength | WinHttpQueryFlagNumber,
                    IntPtr.Zero, ref length, ref size, IntPtr.Zero) && length > maxBytes)
                throw TooLarge(uri, maxBytes);

            var buffer = new byte[1 << 16];
            long total = 0;
            while (true)
            {
                RemainingMs(deadlineUtc, url);
                Check(WinHttpReadData(request, buffer, (uint)buffer.Length, out var read), "WinHttpReadData");
                if (read == 0) break;
                total += read;
                if (total > maxBytes) throw TooLarge(uri, maxBytes);
                destination.Write(buffer, 0, (int)read);
            }
        }
        finally
        {
            if (request != IntPtr.Zero) WinHttpCloseHandle(request);
            if (connection != IntPtr.Zero) WinHttpCloseHandle(connection);
        }
    }

    private static IOException TooLarge(Uri uri, long maxBytes) =>
        new($"{uri.Host} sent more than {maxBytes / (1024 * 1024)} MB for {uri.AbsolutePath}; refusing it.");

    // Opened on first use, so a failure surfaces per request like any other.
    private IntPtr Session()
    {
        if (_session != IntPtr.Zero) return _session;
        // Automatic proxy (Windows 8.1+) reads the same settings as the rest of
        // the system, PAC scripts included; anything older takes the
        // WinHTTP-configured proxy instead.
        _session = WinHttpOpen(UserAgent, WinHttpAccessTypeAutomaticProxy, null, null, 0);
        if (_session == IntPtr.Zero)
            _session = WinHttpOpen(UserAgent, WinHttpAccessTypeDefaultProxy, null, null, 0);
        Check(_session != IntPtr.Zero, "WinHttpOpen");
        return _session;
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
    private const uint WinHttpQueryContentLength = 5;
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
