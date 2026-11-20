// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Web;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.Net;

namespace FileFluss.Core.Providers.GoogleDrive;

/// <summary>
/// Hosts the Google Picker in the user's <b>real browser</b> (port of the macOS
/// <c>GoogleDrivePickerServer</c>). The Picker needs the user's signed-in Google session
/// cookies; an embedded web view has none, and blocked third-party cookies in the Picker
/// iframe produce "Can't access your Google Account". The system browser already has that
/// session.
///
/// <para>Flow: listen on 127.0.0.1, open <c>http://127.0.0.1:&lt;port&gt;/&lt;secret&gt;</c>
/// in the browser (a page that runs the Picker with the supplied OAuth token), and wait for
/// the page to report the chosen folders back via
/// <c>GET /&lt;secret&gt;/result?action=picked&amp;data=…</c>.</para>
/// </summary>
internal sealed class GoogleDrivePickerServer
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private const int MaxRequestBytes = 1 << 20;
    private static readonly TimeSpan RequestReadTimeout = TimeSpan.FromSeconds(30);

    private const string DonePage = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head>"
        + "<body style=\"font-family:'Segoe UI',sans-serif;text-align:center;padding-top:80px;color:#333;\">"
        + "<h2>All set</h2><p>You can close this tab and return to FileFluss.</p></body></html>";

    private readonly Action<Uri> _openBrowser;
    private readonly TimeSpan _timeout;

    /// <param name="openBrowser">Opens the page; defaults to <see cref="BrowserLauncher.Open(Uri)"/>. Tests drive the page over HTTP instead.</param>
    public GoogleDrivePickerServer(Action<Uri>? openBrowser = null, TimeSpan? timeout = null)
    {
        _openBrowser = openBrowser ?? BrowserLauncher.Open;
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>
    /// Opens the Picker in the browser and returns the folders the user chose. Empty when the
    /// user cancelled — and after five minutes without an answer, which counts as a cancel.
    /// </summary>
    public async Task<IReadOnlyList<GoogleDrivePickedRoot>> PickFoldersAsync(string accessToken, CancellationToken cancellationToken)
    {
        // Loopback only. The page embeds the account's live access token, so a listener on
        // every interface would hand that token to anyone on the same network.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
        }
        catch (SocketException e)
        {
            throw CloudProviderException.Network(e);
        }

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        // Unguessable path: a local process that merely knows the port still can't fetch the
        // token page, and a web page probing localhost ports gets a 404.
        var secretPath = "/" + Guid.NewGuid().ToString("D").ToLowerInvariant();
        var page = PickerPage(accessToken, secretPath + "/result");
        var result = new TaskCompletionSource<IReadOnlyList<GoogleDrivePickedRoot>>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stop.CancelAfter(_timeout);
        var acceptLoop = AcceptLoopAsync(listener, secretPath, page, result, stop.Token);
        try
        {
            _openBrowser(new Uri($"http://127.0.0.1:{port}{secretPath}"));
            return await result.Task.WaitAsync(stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            SupportLogger.Shared.Log("Picker timed out — treated as cancelled", "googleDrivePicker", LogLevel.Notice);
            return [];
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            listener.Stop();
            try
            {
                await acceptLoop.ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>
    /// Serves connections concurrently: browsers open speculative connections that may never
    /// carry a request, and one of those must not hold up the real one.
    /// </summary>
    private static async Task AcceptLoopAsync(
        TcpListener listener, string secretPath, string page, TaskCompletionSource<IReadOnlyList<GoogleDrivePickedRoot>> result, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            _ = Task.Run(() => ServeAsync(client, secretPath, page, result, cancellationToken), CancellationToken.None);
        }
    }

    private static async Task ServeAsync(
        TcpClient client, string secretPath, string page, TaskCompletionSource<IReadOnlyList<GoogleDrivePickedRoot>> result, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                // Defence in depth: the loopback binding should make anything else
                // unreachable; refuse rather than serve if it ever isn't.
                if (client.Client.RemoteEndPoint is not IPEndPoint remote || !IPAddress.IsLoopback(remote.Address))
                {
                    return;
                }

                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readTimeout.CancelAfter(RequestReadTimeout);
                var stream = client.GetStream();
                var target = await ReadRequestTargetAsync(stream, readTimeout.Token).ConfigureAwait(false);
                if (target is null)
                {
                    return;
                }

                if (target.StartsWith(secretPath + "/result", StringComparison.Ordinal))
                {
                    var roots = ParseResult(target);
                    await RespondAsync(stream, 200, DonePage, cancellationToken).ConfigureAwait(false);
                    result.TrySetResult(roots);
                    return;
                }

                if (target == secretPath || target.StartsWith(secretPath + "?", StringComparison.Ordinal))
                {
                    await RespondAsync(stream, 200, page, cancellationToken).ConfigureAwait(false);
                    return;
                }

                // favicon and friends
                await RespondAsync(stream, 404, string.Empty, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A browser that hung up, or the server shutting down. One connection's
                // failure must never take the server (or an unobserved task) down with it.
            }
        }
    }

    /// <summary>The folders in <c>?action=picked&amp;data=[{"id":…,"name":…}]</c>; empty for a cancel.</summary>
    internal static IReadOnlyList<GoogleDrivePickedRoot> ParseResult(string target)
    {
        var query = HttpUtility.ParseQueryString(new Uri("http://localhost" + target).Query);
        if (query["action"] != "picked" || query["data"] is not { } data)
        {
            return [];
        }

        try
        {
            var docs = JsonSerializer.Deserialize<List<PickedDoc>>(data, CloudHttp.Json) ?? [];
            return docs
                .Where(d => !string.IsNullOrEmpty(d.Id) && !string.IsNullOrEmpty(d.Name))
                .Select(d => new GoogleDrivePickedRoot(d.Id!, d.Name!))
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Reads the request head (up to the blank line) and returns the request target.</summary>
    private static async Task<string?> ReadRequestTargetAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        // Read the whole head, not just the request line: closing a socket with unread input
        // makes Windows send RST, and the browser may then drop the response.
        var buffer = new byte[64 * 1024];
        using var head = new MemoryStream();
        while (head.Length < MaxRequestBytes)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            head.Write(buffer, 0, read);
            if (Encoding.ASCII.GetString(head.GetBuffer(), 0, (int)head.Length).Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                break;
            }
        }

        var requestLine = Encoding.ASCII.GetString(head.GetBuffer(), 0, (int)head.Length).Split("\r\n")[0];
        var parts = requestLine.Split(' ');
        return parts.Length >= 2 && parts[1].StartsWith('/') ? parts[1] : null;
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string html, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(html);
        var reason = status == 200 ? "OK" : "Not Found";
        var header = $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The page that runs the Picker. A navigable Docs view (My Drive → subfolders) showing
    /// folders only, rather than <c>ViewId.FOLDERS</c>, which dumps every folder into one
    /// flat, unordered list. <c>setAppId</c> is essential: it ties the <c>drive.file</c>
    /// grant to this app's OAuth client — without it the picked folders list empty.
    /// The values are embedded as JSON string literals (which escape &lt; and &gt;), so
    /// nothing in them can break out of the script.
    /// </summary>
    internal static string PickerPage(string accessToken, string resultPath)
    {
        var token = JsonSerializer.Serialize(accessToken);
        var result = JsonSerializer.Serialize(resultPath);
        var apiKey = JsonSerializer.Serialize(GoogleDrivePickerProvider.PickerApiKey);
        var appId = JsonSerializer.Serialize(GoogleDrivePickerProvider.PickerAppId);
        return $$"""
            <!DOCTYPE html>
            <html>
            <head><meta charset="utf-8"><title>FileFluss — choose folders</title></head>
            <body style="font-family:'Segoe UI',sans-serif;padding:24px;color:#333;">
            <p id="status">Loading Google Picker… If nothing appears, make sure you're signed in to Google in this browser.</p>
            <script src="https://apis.google.com/js/api.js"></script>
            <script>
              const TOKEN = {{token}};
              const RESULT_PATH = {{result}};
              const APIKEY = {{apiKey}};
              const APPID = {{appId}};
              function report(action, docs){
                const p = new URLSearchParams();
                p.set('action', action);
                if (docs) p.set('data', JSON.stringify(docs));
                fetch(RESULT_PATH + '?' + p.toString()).then(function(){
                  document.getElementById('status').textContent = 'Done — you can close this tab and return to FileFluss.';
                });
              }
              function onApiLoad(){ gapi.load('picker', { callback: createPicker }); }
              function createPicker(){
                try {
                  document.getElementById('status').textContent = 'Browse into My Drive, select the folders FileFluss may access, then click Select.';
                  const view = new google.picker.DocsView(google.picker.ViewId.DOCS)
                      .setIncludeFolders(true)
                      .setSelectFolderEnabled(true)
                      .setMimeTypes('application/vnd.google-apps.folder')
                      .setParent('root');
                  const picker = new google.picker.PickerBuilder()
                      .enableFeature(google.picker.Feature.MULTISELECT_ENABLED)
                      .setAppId(APPID)
                      .setOAuthToken(TOKEN)
                      .setDeveloperKey(APIKEY)
                      .addView(view)
                      .setTitle('Select folders for FileFluss')
                      .setCallback(function(data){
                        const action = data[google.picker.Response.ACTION];
                        if (action == google.picker.Action.PICKED){
                          const docs = (data[google.picker.Response.DOCUMENTS]||[]).map(function(d){
                            return { id: d[google.picker.Document.ID], name: d[google.picker.Document.NAME] };
                          });
                          report('picked', docs);
                        } else if (action == google.picker.Action.CANCEL){
                          report('cancel', null);
                        }
                      })
                      .build();
                  picker.setVisible(true);
                } catch(e){ document.getElementById('status').textContent = 'Picker error: ' + e; }
              }
              window.onload = onApiLoad;
            </script>
            </body>
            </html>
            """;
    }

    private sealed class PickedDoc
    {
        public string? Id { get; init; }

        public string? Name { get; init; }
    }
}
