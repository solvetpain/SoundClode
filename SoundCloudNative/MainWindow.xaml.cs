using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Interop;
using Forms = System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace SoundCloudNative
{
    public partial class MainWindow : Window
    {
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        private string _activeClientId = "tHVbSbDP4TaoMbPppngv6Tdry83vNbMQ";
        private DiscordRpcClient? _discordRpc;
        private CoreWebView2Environment? _env;
        private Window? _currentAuthWin;

        private Forms.NotifyIcon? _notifyIcon;
        private Forms.ToolStripMenuItem? _trayTrackItem;
        private bool _isQuitting = false;
        private bool _minimizeToTray = true;

        private async Task<string> FetchFreshClientIdAsync()
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://soundcloud.com");
                req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
                var resp = await _httpClient.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    var html = await resp.Content.ReadAsStringAsync();
                    var matches = System.Text.RegularExpressions.Regex.Matches(html, @"https://a-v2\.sndcdn\.com/assets/[^""]+\.js");
                    for (int i = matches.Count - 1; i >= Math.Max(0, matches.Count - 8); i--)
                    {
                        using var jsReq = new HttpRequestMessage(HttpMethod.Get, matches[i].Value);
                        jsReq.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                        var jsResp = await _httpClient.SendAsync(jsReq);
                        if (jsResp.IsSuccessStatusCode)
                        {
                            var js = await jsResp.Content.ReadAsStringAsync();
                            var m = System.Text.RegularExpressions.Regex.Match(js, @"client_id[:=][""']?([a-zA-Z0-9]{32})[""']?");
                            if (m.Success)
                            {
                                _activeClientId = m.Groups[1].Value;
                                Debug.WriteLine("[SoundClode] Scraped fresh client_id: " + _activeClientId);
                                return _activeClientId;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[SoundClode] Error fetching fresh client_id: " + ex.Message);
            }
            return _activeClientId;
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);

        [StructLayout(LayoutKind.Sequential)]
        private struct MARGINS
        {
            public int cxLeftWidth;
            public int cxRightWidth;
            public int cyTopHeight;
            public int cyBottomHeight;
        }

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_MICA_EFFECT = 1029;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

        private const uint IMAGE_ICON = 1;
        private const uint LR_LOADFROMFILE = 0x00000010;
        private const uint WM_SETICON = 0x0080;
        private const int ICON_SMALL = 0;
        private const int ICON_BIG = 1;

        private static string GetOrExtractAssetPath(string fileName)
        {
            try
            {
                var localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
                if (File.Exists(localPath)) return localPath;

                var assetsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundClode", "Assets");
                Directory.CreateDirectory(assetsDir);
                var targetPath = Path.Combine(assetsDir, fileName);

                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                var resName = System.Linq.Enumerable.FirstOrDefault(asm.GetManifestResourceNames(), n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
                if (resName != null)
                {
                    using var s = asm.GetManifestResourceStream(resName);
                    if (s != null)
                    {
                        using var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write);
                        s.CopyTo(fs);
                    }
                }
                return File.Exists(targetPath) ? targetPath : localPath;
            }
            catch
            {
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
            }
        }

        public MainWindow()
        {
            InitializeComponent();

            _discordRpc = new DiscordRpcClient();
            _discordRpc.Initialize();

            try
            {
                var icoPath = GetOrExtractAssetPath("soundcloud.ico");
                if (File.Exists(icoPath))
                {
                    Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(icoPath));
                }
            }
            catch {}

            SourceInitialized += (s, e) =>
            {
                try
                {
                    var helper = new System.Windows.Interop.WindowInteropHelper(this);
                    IntPtr hWnd = helper.Handle;
                    if (hWnd != IntPtr.Zero)
                    {
                        // 1. Windows 11 Immersive Dark Mode for frames
                        int darkMode = 1;
                        DwmSetWindowAttribute(hWnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

                        // 2. Windows 11 Mica Backdrop (2 = Mica, 4 = Mica Tabbed / Alt, 3 = Acrylic)
                        int backdrop = 2;
                        int hr = DwmSetWindowAttribute(hWnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
                        if (hr != 0)
                        {
                            // Fallback for Windows 11 21H2 Mica
                            int mica = 1;
                            DwmSetWindowAttribute(hWnd, DWMWA_MICA_EFFECT, ref mica, sizeof(int));
                        }

                        // 3. Extend Frame Into Client Area for full glass effect
                        var margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
                        DwmExtendFrameIntoClientArea(hWnd, ref margins);

                        // 4. Set Application Icons
                        var icoPath = GetOrExtractAssetPath("soundcloud.ico");
                        if (File.Exists(icoPath))
                        {
                            IntPtr hSmall = LoadImage(IntPtr.Zero, icoPath, IMAGE_ICON, 24, 24, LR_LOADFROMFILE);
                            if (hSmall != IntPtr.Zero) SendMessage(hWnd, WM_SETICON, (IntPtr)ICON_SMALL, hSmall);

                            IntPtr hBig = LoadImage(IntPtr.Zero, icoPath, IMAGE_ICON, 32, 32, LR_LOADFROMFILE);
                            if (hBig != IntPtr.Zero) SendMessage(hWnd, WM_SETICON, (IntPtr)ICON_BIG, hBig);
                        }
                    }
                }
                catch {}
            };

            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            try
            {
                if (_notifyIcon != null)
                {
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                    _notifyIcon = null;
                }
                _discordRpc?.ClearActivity();
                _discordRpc?.Dispose();
            }
            catch { }
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                InitializeNotifyIcon();

                var userDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundClode", "WebViewProfile");
                var options = new CoreWebView2EnvironmentOptions
                {
                    AdditionalBrowserArguments =
                        "--disable-features=CalculateNativeWinOcclusion,IntensiveWakeUpThrottling " +
                        "--disable-backgrounding-occluded-windows " +
                        "--disable-background-timer-throttling " +
                        "--disable-renderer-backgrounding " +
                        "--disable-gpu-process-crash-limit " +
                        "--autoplay-policy=no-user-gesture-required"
                };
                _env = await CoreWebView2Environment.CreateAsync(null, userDataDir, options);
                await webView.EnsureCoreWebView2Async(_env);

                webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0, 0, 0, 0);

                // Auto-recover if GPU or Renderer crashes (e.g. when DirectX games take exclusive focus)
                webView.CoreWebView2.ProcessFailed += (s, args) =>
                {
                    Debug.WriteLine($"[SoundClode] ProcessFailed: {args.ProcessFailedKind}, reason: {args.Reason}");
                    if (args.ProcessFailedKind == CoreWebView2ProcessFailedKind.GpuProcessExited)
                    {
                        try { webView.CoreWebView2.Resume(); } catch { }
                    }
                    else
                    {
                        Dispatcher.Invoke(() =>
                        {
                            try { webView.Reload(); } catch { }
                        });
                    }
                };

                // Initialize background sync worker sharing same profile
                await bgSyncWebView.EnsureCoreWebView2Async(_env);
                bgSyncWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                bgSyncWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                bgSyncWebView.CoreWebView2.WebMessageReceived += (s, args) =>
                {
                    try
                    {
                        // Forward like results or background messages to frontend
                        webView.CoreWebView2.PostWebMessageAsJson(args.WebMessageAsJson);
                    }
                    catch { }
                };

                // Warm up DataDome session in background
                _ = EnsureBgSyncReadyAsync();

                // Proxy SoundCloud API, Waveform, and asset requests to bypass any CORS restrictions
                webView.CoreWebView2.AddWebResourceRequestedFilter("https://soundclode.local/*", CoreWebView2WebResourceContext.All);
                webView.CoreWebView2.AddWebResourceRequestedFilter("https://api-v2.soundcloud.com/*", CoreWebView2WebResourceContext.All);
                webView.CoreWebView2.AddWebResourceRequestedFilter("https://wave.sndcdn.com/*", CoreWebView2WebResourceContext.All);
                webView.CoreWebView2.AddWebResourceRequestedFilter("https://soundcloud.com/*", CoreWebView2WebResourceContext.All);
                webView.CoreWebView2.AddWebResourceRequestedFilter("https://a-v2.sndcdn.com/*", CoreWebView2WebResourceContext.All);

                webView.CoreWebView2.WebResourceRequested += async (s, args) =>
                {
                    var deferral = args.GetDeferral();
                    try
                    {
                        if (args.Request.Uri.StartsWith("https://soundclode.local", StringComparison.OrdinalIgnoreCase))
                        {
                            var localUri = new Uri(args.Request.Uri);
                            var localPath = localUri.AbsolutePath.TrimStart('/');
                            if (string.IsNullOrEmpty(localPath) || localPath.Equals("index.html", StringComparison.OrdinalIgnoreCase))
                            {
                                var localDiskPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "index.html");
                                if (!File.Exists(localDiskPath))
                                {
                                    localDiskPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundClode", "Assets", "index.html");
                                }
                                if (File.Exists(localDiskPath))
                                {
                                    var localBytes = File.ReadAllBytes(localDiskPath);
                                    var localHeaders = "Access-Control-Allow-Origin: *\r\nAccess-Control-Allow-Headers: *\r\nAccess-Control-Allow-Methods: *\r\nContent-Type: text/html; charset=utf-8\r\n";
                                    args.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(localBytes), 200, "OK", localHeaders);
                                    return;
                                }

                                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                                var htmlRes = System.Linq.Enumerable.FirstOrDefault(asm.GetManifestResourceNames(), n => n.EndsWith("index.html", StringComparison.OrdinalIgnoreCase));
                                if (htmlRes != null)
                                {
                                    var resStream = asm.GetManifestResourceStream(htmlRes);
                                    if (resStream != null)
                                    {
                                        var localHeaders = "Access-Control-Allow-Origin: *\r\nAccess-Control-Allow-Headers: *\r\nAccess-Control-Allow-Methods: *\r\nContent-Type: text/html; charset=utf-8\r\n";
                                        args.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(resStream, 200, "OK", localHeaders);
                                        return;
                                    }
                                }
                            }
                        }

                        if (args.Request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
                        {
                            var corsHeaders = "Access-Control-Allow-Origin: *\r\nAccess-Control-Allow-Headers: *\r\nAccess-Control-Allow-Methods: GET, POST, PUT, DELETE, OPTIONS\r\nAccess-Control-Max-Age: 86400\r\nContent-Type: text/plain\r\n";
                            args.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(), 200, "OK", corsHeaders);
                            return;
                        }

                        var method = new HttpMethod(args.Request.Method);
                        using var req = new HttpRequestMessage(method, args.Request.Uri);
                        req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");

                        // Forward Authorization header if present
                        if (args.Request.Headers.Contains("Authorization"))
                        {
                            req.Headers.Add("Authorization", args.Request.Headers.GetHeader("Authorization"));
                        }

                        if (args.Request.Content != null && (method == HttpMethod.Post || method == HttpMethod.Put))
                        {
                            req.Content = new StreamContent(args.Request.Content);
                            if (args.Request.Headers.Contains("Content-Type"))
                            {
                                req.Content.Headers.TryAddWithoutValidation("Content-Type", args.Request.Headers.GetHeader("Content-Type"));
                            }
                        }

                        var resp = await _httpClient.SendAsync(req);
                        var stream = await resp.Content.ReadAsStreamAsync();
                        var contentType = resp.Content.Headers.ContentType?.ToString() ?? (args.Request.Uri.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ? "application/javascript; charset=utf-8" : "application/json; charset=utf-8");
                        var headers = $"Access-Control-Allow-Origin: *\r\nAccess-Control-Allow-Headers: *\r\nAccess-Control-Allow-Methods: *\r\nContent-Type: {contentType}\r\n";
                        args.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(stream, (int)resp.StatusCode, resp.ReasonPhrase, headers);
                    }
                    catch (Exception ex)
                    {
                        var errBytes = System.Text.Encoding.UTF8.GetBytes($"{{\"error\":\"{ex.Message}\"}}");
                        var headers = "Access-Control-Allow-Origin: *\r\nAccess-Control-Allow-Headers: *\r\nAccess-Control-Allow-Methods: *\r\nContent-Type: application/json; charset=utf-8\r\n";
                        args.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(errBytes), 502, "Bad Gateway", headers);
                    }
                    finally
                    {
                        deferral.Complete();
                    }
                };

                // Handle IPC messages from HTML client (Discord RPC, Auth, Likes, Client ID, etc.)
                webView.CoreWebView2.WebMessageReceived += (s, args) =>
                {
                    try
                    {
                        var json = args.WebMessageAsJson;
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("type", out var typeProp))
                        {
                            var type = typeProp.GetString();
                            if (type == "GET_CLIENT_ID")
                            {
                                _ = Task.Run(async () =>
                                {
                                    var cid = await FetchFreshClientIdAsync();
                                    Dispatcher.Invoke(() =>
                                    {
                                        try
                                        {
                                            webView.CoreWebView2.PostWebMessageAsJson($"{{\"type\":\"CLIENT_ID\",\"clientId\":\"{cid}\"}}");
                                        }
                                        catch { }
                                    });
                                });
                            }
                            else if (type == "DISCORD_RPC")
                            {
                                if (root.TryGetProperty("action", out var actProp) && actProp.GetString() == "CLEAR")
                                {
                                    _discordRpc?.ClearActivity();
                                    if (_trayTrackItem != null) _trayTrackItem.Text = "SoundClode — Ничего не играет";
                                    if (_notifyIcon != null) _notifyIcon.Text = "SoundClode";
                                }
                                else
                                {
                                    string title = root.TryGetProperty("title", out var tProp) ? tProp.GetString() ?? "" : "";
                                    string artist = root.TryGetProperty("artist", out var aProp) ? aProp.GetString() ?? "" : "";
                                    string? artwork = root.TryGetProperty("artwork", out var artProp) ? artProp.GetString() : null;
                                    double duration = root.TryGetProperty("duration", out var dProp) ? dProp.GetDouble() : 0;
                                    double current = root.TryGetProperty("currentTime", out var cProp) ? cProp.GetDouble() : 0;
                                    bool playing = root.TryGetProperty("playing", out var pProp) && pProp.GetBoolean();

                                    _discordRpc?.UpdateActivity(title, artist, artwork, duration, current, playing);

                                    if (!string.IsNullOrEmpty(title))
                                    {
                                        string displayStr = $"{title} — {artist}";
                                        if (_trayTrackItem != null) _trayTrackItem.Text = displayStr.Length > 45 ? displayStr.Substring(0, 42) + "..." : displayStr;
                                        if (_notifyIcon != null)
                                        {
                                            _notifyIcon.Text = displayStr.Substring(0, Math.Min(63, displayStr.Length));
                                        }
                                    }
                                }
                            }
                            else if (type == "TRACK_NOTIFICATION")
                            {
                                string title = root.TryGetProperty("title", out var tP) ? tP.GetString() ?? "" : "";
                                string artist = root.TryGetProperty("artist", out var aP) ? aP.GetString() ?? "" : "";
                                if (_notifyIcon != null && !IsVisible && !string.IsNullOrEmpty(title))
                                {
                                    _notifyIcon.ShowBalloonTip(2500, "Сейчас играет", $"{title}\n{artist}", Forms.ToolTipIcon.None);
                                }
                            }
                            else if (type == "THEME_CHANGED")
                            {
                                string thm = root.TryGetProperty("theme", out var thmProp) ? thmProp.GetString() ?? "dark" : "dark";
                                ApplyWindowTheme(thm);
                            }
                            else if (type == "LIKE_TRACK" || type == "LIKE_PLAYLIST")
                            {
                                long id = root.TryGetProperty("id", out var idProp) ? idProp.GetInt64() : (root.TryGetProperty("trackId", out var tidProp) ? tidProp.GetInt64() : (root.TryGetProperty("playlistId", out var pidProp) ? pidProp.GetInt64() : 0));
                                bool isLiked = root.TryGetProperty("isLiked", out var likProp) && likProp.GetBoolean();
                                string? token = root.TryGetProperty("token", out var tokProp) ? tokProp.GetString() : null;
                                long userId = root.TryGetProperty("userId", out var uidProp) ? uidProp.GetInt64() : 0;
                                bool isPlaylist = (type == "LIKE_PLAYLIST") || (root.TryGetProperty("isPlaylist", out var plProp) && plProp.GetBoolean());

                                if (id > 0 && userId > 0 && !string.IsNullOrEmpty(token))
                                {
                                    _ = ExecuteLikeAsync(id, isLiked, isPlaylist, token, userId);
                                }
                            }
                            else if (type == "SET_OAUTH_TOKEN")
                            {
                                string? token = root.TryGetProperty("token", out var tokProp) ? tokProp.GetString() : null;
                                if (!string.IsNullOrEmpty(token))
                                {
                                    _bgSyncReady = false;
                                    _ = EnsureBgSyncReadyAsync(token);
                                }
                            }
                            else if (type == "OPEN_AUTH")
                            {
                                string? provider = root.TryGetProperty("provider", out var p) ? p.GetString() : null;
                                OpenAuthWindow(provider);
                            }
                            else if (type == "LOGOUT")
                            {
                                _discordRpc?.ClearActivity();
                                _bgSyncReady = false;
                                _cachedOAuthToken = null;
                                webView.CoreWebView2.CookieManager.DeleteAllCookies();
                                bgSyncWebView.CoreWebView2.CookieManager.DeleteAllCookies();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("WebMessage error: " + ex.Message);
                    }
                };

                var localHtml = GetOrExtractAssetPath("index.html");
                var htmlFolder = Path.GetDirectoryName(localHtml) ?? AppDomain.CurrentDomain.BaseDirectory;
                webView.CoreWebView2.SetVirtualHostNameToFolderMapping("soundclode.local", htmlFolder, CoreWebView2HostResourceAccessKind.Allow);
                webView.Source = new Uri("https://soundclode.local/index.html");

                webView.NavigationCompleted += async (s, e) =>
                {
                    try
                    {
                        var themeJson = await webView.ExecuteScriptAsync("document.documentElement.getAttribute('data-theme') || 'dark'");
                        var theme = themeJson?.Trim('"', ' ', '\\') ?? "dark";
                        ApplyWindowTheme(theme);
                    }
                    catch { }
                };

                // Broadcast fresh scraped client ID on launch
                _ = Task.Run(async () =>
                {
                    var cid = await FetchFreshClientIdAsync();
                    await Task.Delay(1500);
                    Dispatcher.Invoke(() =>
                    {
                        try
                        {
                            webView.CoreWebView2.PostWebMessageAsJson($"{{\"type\":\"CLIENT_ID\",\"clientId\":\"{cid}\"}}");
                        }
                        catch { }
                    });
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка запуска SoundClode: " + ex.Message);
            }
        }

        private bool _bgSyncReady = false;
        private string? _cachedOAuthToken = null;

        private async Task EnsureBgSyncReadyAsync(string? token = null)
        {
            try
            {
                if (!string.IsNullOrEmpty(token))
                {
                    _cachedOAuthToken = token;
                    var oauthCookie = bgSyncWebView.CoreWebView2.CookieManager.CreateCookie("oauth_token", token, ".soundcloud.com", "/");
                    oauthCookie.IsSecure = true;
                    oauthCookie.SameSite = CoreWebView2CookieSameSiteKind.Lax;
                    bgSyncWebView.CoreWebView2.CookieManager.AddOrUpdateCookie(oauthCookie);
                }

                if (!_bgSyncReady)
                {
                    var tcs = new TaskCompletionSource<bool>();
                    void OnNavCompleted(object? s, CoreWebView2NavigationCompletedEventArgs e)
                    {
                        bgSyncWebView.NavigationCompleted -= OnNavCompleted;
                        tcs.TrySetResult(true);
                    }
                    bgSyncWebView.NavigationCompleted += OnNavCompleted;
                    bgSyncWebView.CoreWebView2.Navigate("https://soundcloud.com");

                    await Task.WhenAny(tcs.Task, Task.Delay(6000));
                    await Task.Delay(2000); // Allow DataDome JavaScript to execute and issue datadome cookie
                    _bgSyncReady = true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[SoundClode] BgSync error: " + ex.Message);
            }
        }

        private async Task ExecuteLikeAsync(long id, bool isLiked, bool isPlaylist, string token, long userId)
        {
            try
            {
                await EnsureBgSyncReadyAsync(token);

                string endpoint = isPlaylist ? $"playlist_likes/{id}" : $"track_likes/{id}";
                string method = isLiked ? "PUT" : "DELETE";
                string isLikedStr = isLiked ? "true" : "false";
                string isPlaylistStr = isPlaylist ? "true" : "false";
                string script = $@"
                    (async () => {{
                        try {{
                            const r = await fetch('https://api-v2.soundcloud.com/users/{userId}/{endpoint}?client_id=tHVbSbDP4TaoMbPppngv6Tdry83vNbMQ', {{
                                method: '{method}',
                                headers: {{
                                    'Authorization': 'OAuth {token}',
                                    'Accept': 'application/json, text/javascript, */*; q=0.01'
                                }}
                            }});
                            const body = await r.text();
                            window.chrome.webview.postMessage(JSON.stringify({{
                                type: 'LIKE_RESULT',
                                id: {id},
                                trackId: {id},
                                isPlaylist: {isPlaylistStr},
                                isLiked: {isLikedStr},
                                status: r.status,
                                ok: r.ok,
                                body: body
                            }}));
                        }} catch(e) {{
                            window.chrome.webview.postMessage(JSON.stringify({{
                                type: 'LIKE_RESULT',
                                id: {id},
                                trackId: {id},
                                isPlaylist: {isPlaylistStr},
                                isLiked: {isLikedStr},
                                ok: false,
                                error: (e || '').toString()
                            }}));
                        }}
                    }})()
                ";

                if (bgSyncWebView?.CoreWebView2 != null)
                {
                    await bgSyncWebView.CoreWebView2.ExecuteScriptAsync(script);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[SoundClode] Like execution error: " + ex.Message);
            }
        }

        private void OpenAuthWindow(string? provider = null)
        {
            Dispatcher.Invoke(async () =>
            {
                try
                {
                    if (_currentAuthWin != null && _currentAuthWin.IsVisible)
                    {
                        _currentAuthWin.Activate();
                        return;
                    }

                    if (_env == null)
                    {
                        var userDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundClode", "WebViewProfile");
                        var options = new CoreWebView2EnvironmentOptions();
                        _env = await CoreWebView2Environment.CreateAsync(null, userDataDir, options);
                    }

                    // Window sized comfortably to fit SoundCloud's 800px modal without scrollbars
                    var authWin = new Window
                    {
                        Title = "SoundClode — Вход в SoundCloud",
                        Width = 860,
                        Height = 740,
                        MinWidth = 760,
                        MinHeight = 600,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        Owner = this,
                        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(18, 19, 24)),
                        ResizeMode = ResizeMode.CanResize
                    };
                    _currentAuthWin = authWin;

                    var authWebView = new Microsoft.Web.WebView2.Wpf.WebView2();
                    authWin.Content = authWebView;

                    Window? currentPopup = null;
                    authWin.Closed += (s, e) =>
                    {
                        try { currentPopup?.Close(); } catch { }
                        _currentAuthWin = null;
                    };
                    authWin.Show();

                    await authWebView.EnsureCoreWebView2Async(_env);

                    authWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                    authWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                    authWebView.CoreWebView2.Settings.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";
                    await authWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync("try { delete window.chrome.webview; } catch(e){} Object.defineProperty(navigator, 'webdriver', {get: () => undefined});");

                    bool authCompleted = false;
                    async Task HandleTokenFound(string oauthToken)
                    {
                        if (authCompleted || string.IsNullOrWhiteSpace(oauthToken)) return;
                        authCompleted = true;

                        try
                        {
                            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api-v2.soundcloud.com/me");
                            req.Headers.Add("Authorization", "OAuth " + oauthToken);
                            req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
                            var resp = await _httpClient.SendAsync(req);
                            if (resp.IsSuccessStatusCode)
                            {
                                var userJson = await resp.Content.ReadAsStringAsync();
                                Dispatcher.Invoke(() =>
                                {
                                    webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
                                    {
                                        type = "AUTH_SUCCESS",
                                        token = oauthToken,
                                        user = JsonSerializer.Deserialize<JsonElement>(userJson)
                                    }));
                                    try { currentPopup?.Close(); } catch { }
                                    try { authWin.Close(); } catch { }
                                });
                            }
                            else
                            {
                                authCompleted = false;
                            }
                        }
                        catch
                        {
                            authCompleted = false;
                        }
                    }

                    // Intercept network requests for immediate Authorization OAuth header detection
                    authWebView.CoreWebView2.WebResourceResponseReceived += async (s, e) =>
                    {
                        try
                        {
                            if (e.Request.Headers.Contains("Authorization"))
                            {
                                var auth = e.Request.Headers.GetHeader("Authorization");
                                if (auth.StartsWith("OAuth ", StringComparison.OrdinalIgnoreCase))
                                {
                                    var tok = auth.Substring(6).Trim();
                                    if (!string.IsNullOrEmpty(tok))
                                    {
                                        await HandleTokenFound(tok);
                                    }
                                }
                            }
                        }
                        catch { }
                    };

                    // Handle OAuth popups (Google, Apple, Facebook) into a proper separate popup window
                    authWebView.CoreWebView2.NewWindowRequested += async (sender, args) =>
                    {
                        var deferral = args.GetDeferral();
                        try
                        {
                            var popupWin = new Window
                            {
                                Title = "SoundClode — Google / Apple Авторизация",
                                Width = 560,
                                Height = 700,
                                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                                Owner = authWin,
                                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White),
                                ResizeMode = ResizeMode.CanResize
                            };
                            currentPopup = popupWin;

                            var popupWv = new Microsoft.Web.WebView2.Wpf.WebView2();
                            popupWin.Content = popupWv;

                            await popupWv.EnsureCoreWebView2Async(_env);

                            popupWv.CoreWebView2.Settings.IsStatusBarEnabled = false;
                            popupWv.CoreWebView2.Settings.AreDevToolsEnabled = false;
                            popupWv.CoreWebView2.Settings.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";
                            await popupWv.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync("try { delete window.chrome.webview; } catch(e){} Object.defineProperty(navigator, 'webdriver', {get: () => undefined});");

                            popupWv.CoreWebView2.WindowCloseRequested += (s, e) =>
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    try { popupWin.Close(); } catch { }
                                });
                            };

                            popupWv.CoreWebView2.WebResourceResponseReceived += async (s, e) =>
                            {
                                try
                                {
                                    if (e.Request.Headers.Contains("Authorization"))
                                    {
                                        var auth = e.Request.Headers.GetHeader("Authorization");
                                        if (auth.StartsWith("OAuth ", StringComparison.OrdinalIgnoreCase))
                                        {
                                            var tok = auth.Substring(6).Trim();
                                            if (!string.IsNullOrEmpty(tok))
                                            {
                                                await HandleTokenFound(tok);
                                            }
                                        }
                                    }
                                }
                                catch { }
                            };

                            args.NewWindow = popupWv.CoreWebView2;
                            popupWin.Show();
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine("[SoundClode] Popup error: " + ex.Message);
                        }
                        finally
                        {
                            deferral.Complete();
                        }
                    };

                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                    timer.Tick += async (ts, te) =>
                    {
                        if (authCompleted) return;
                        try
                        {
                            var cookies = await authWebView.CoreWebView2.CookieManager.GetCookiesAsync("");
                            string? oauthToken = null;
                            foreach (var c in cookies)
                            {
                                if (c.Name == "oauth_token" && !string.IsNullOrWhiteSpace(c.Value))
                                {
                                    oauthToken = c.Value;
                                    break;
                                }
                            }

                            if (!string.IsNullOrEmpty(oauthToken))
                            {
                                await HandleTokenFound(oauthToken);
                            }
                        }
                        catch { }
                    };
                    timer.Start();

                    authWin.Closed += (cs, ce) => timer.Stop();

                    authWebView.CoreWebView2.Navigate("https://soundcloud.com/signin");
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Ошибка окна авторизации: " + ex.Message);
                }
            });
        }

        private void InitializeNotifyIcon()
        {
            try
            {
                var icoPath = GetOrExtractAssetPath("soundcloud.ico");
                var icon = File.Exists(icoPath) ? new System.Drawing.Icon(icoPath) : System.Drawing.SystemIcons.Application;

                _notifyIcon = new Forms.NotifyIcon
                {
                    Icon = icon,
                    Text = "SoundClode",
                    Visible = true
                };

                var menu = new Forms.ContextMenuStrip();

                _trayTrackItem = new Forms.ToolStripMenuItem("SoundClode — Ничего не играет") { Enabled = false };
                menu.Items.Add(_trayTrackItem);
                menu.Items.Add(new Forms.ToolStripSeparator());

                var itemPlay = new Forms.ToolStripMenuItem("Воспроизведение / Пауза", null, (s, e) => SendFrontendCommand("TOGGLE_PLAY"));
                var itemNext = new Forms.ToolStripMenuItem("Следующий трек", null, (s, e) => SendFrontendCommand("PLAY_NEXT"));
                var itemPrev = new Forms.ToolStripMenuItem("Предыдущий трек", null, (s, e) => SendFrontendCommand("PLAY_PREV"));
                menu.Items.Add(itemPlay);
                menu.Items.Add(itemNext);
                menu.Items.Add(itemPrev);
                menu.Items.Add(new Forms.ToolStripSeparator());

                var itemShow = new Forms.ToolStripMenuItem("Открыть плеер", null, (s, e) => RestoreWindow());
                var itemExit = new Forms.ToolStripMenuItem("Выход", null, (s, e) =>
                {
                    _isQuitting = true;
                    Close();
                });
                menu.Items.Add(itemShow);
                menu.Items.Add(itemExit);

                _notifyIcon.ContextMenuStrip = menu;
                _notifyIcon.DoubleClick += (s, e) => RestoreWindow();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[SoundClode] NotifyIcon init error: " + ex.Message);
            }
        }

        private void SendFrontendCommand(string command)
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    webView?.CoreWebView2?.PostWebMessageAsJson($"{{\"type\":\"COMMAND\",\"command\":\"{command}\"}}");
                }
                catch { }
            });
        }

        private void RestoreWindow()
        {
            Dispatcher.Invoke(() =>
            {
                Show();
                WindowState = WindowState.Normal;
                Activate();
                Focus();
            });
        }

        private void ApplyWindowTheme(string theme)
        {
            Dispatcher.Invoke(() =>
            {
                bool isLight = string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase);
                if (isLight)
                {
                    mainBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF7, 0xF8, 0xFA));
                    mainBorder.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE2, 0xE4, 0xEA));
                    titleBarGrid.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF7, 0xF8, 0xFA));
                    btnMinimize.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x40, 0x42, 0x4C));
                    btnMaximize.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x40, 0x42, 0x4C));
                    btnClose.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x40, 0x42, 0x4C));

                    int darkMode = 0;
                    var hwnd = new WindowInteropHelper(this).Handle;
                    if (hwnd != IntPtr.Zero)
                    {
                        try { DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int)); } catch { }
                    }
                }
                else
                {
                    mainBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xC0, 0x07, 0x08, 0x0A));
                    mainBorder.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x20, 0x22, 0x2B));
                    titleBarGrid.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x90, 0x07, 0x08, 0x0A));
                    btnMinimize.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x71, 0x75, 0x85));
                    btnMaximize.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x71, 0x75, 0x85));
                    btnClose.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x71, 0x75, 0x85));

                    int darkMode = 1;
                    var hwnd = new WindowInteropHelper(this).Handle;
                    if (hwnd != IntPtr.Zero)
                    {
                        try { DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int)); } catch { }
                    }
                }
            });
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_isQuitting && _minimizeToTray)
            {
                e.Cancel = true;
                Hide();
                if (_notifyIcon != null)
                {
                    _notifyIcon.ShowBalloonTip(1500, "SoundClode", "Приложение продолжает играть в трее", Forms.ToolTipIcon.Info);
                }
                return;
            }
            base.OnClosing(e);
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                if (e.ClickCount == 2)
                {
                    WindowState = (WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;
                }
                else
                {
                    DragMove();
                }
            }
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            if (_minimizeToTray)
            {
                Hide();
                if (_notifyIcon != null)
                {
                    _notifyIcon.ShowBalloonTip(1500, "SoundClode", "Приложение свернуто в трей", Forms.ToolTipIcon.Info);
                }
            }
            else
            {
                WindowState = WindowState.Minimized;
            }
        }

        private void BtnMaximize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = (WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            if (_minimizeToTray && !_isQuitting)
            {
                Hide();
                if (_notifyIcon != null)
                {
                    _notifyIcon.ShowBalloonTip(1500, "SoundClode", "Приложение свернуто в трей", Forms.ToolTipIcon.Info);
                }
            }
            else
            {
                Close();
            }
        }
    }
}
