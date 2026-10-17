using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace SoundCloudNative
{
    public partial class MainWindow : Window
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        private DiscordRpcClient? _discordRpc;
        private CoreWebView2Environment? _env;
        private Window? _currentAuthWin;

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private const uint IMAGE_ICON = 1;
        private const uint LR_LOADFROMFILE = 0x00000010;
        private const uint WM_SETICON = 0x0080;
        private const int ICON_SMALL = 0;
        private const int ICON_BIG = 1;

        public MainWindow()
        {
            InitializeComponent();

            _discordRpc = new DiscordRpcClient();
            _discordRpc.Initialize();

            try
            {
                var icoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "soundcloud.ico");
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
                    var icoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "soundcloud.ico");
                    if (File.Exists(icoPath))
                    {
                        var helper = new System.Windows.Interop.WindowInteropHelper(this);
                        IntPtr hWnd = helper.Handle;
                        if (hWnd != IntPtr.Zero)
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
                _discordRpc?.ClearActivity();
                _discordRpc?.Dispose();
            }
            catch { }
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var userDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundClode", "WebViewProfile");
                var options = new CoreWebView2EnvironmentOptions("--disable-web-security --allow-running-insecure-content");
                _env = await CoreWebView2Environment.CreateAsync(null, userDataDir, options);
                await webView.EnsureCoreWebView2Async(_env);

                webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                webView.CoreWebView2.Settings.AreDevToolsEnabled = false;

                // Proxy SoundCloud API and Waveform requests to bypass any CORS restrictions
                webView.CoreWebView2.AddWebResourceRequestedFilter("https://api-v2.soundcloud.com/*", CoreWebView2WebResourceContext.All);
                webView.CoreWebView2.AddWebResourceRequestedFilter("https://wave.sndcdn.com/*", CoreWebView2WebResourceContext.All);

                webView.CoreWebView2.WebResourceRequested += async (s, args) =>
                {
                    var deferral = args.GetDeferral();
                    try
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Get, args.Request.Uri);
                        req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");

                        // Forward Authorization header if present
                        if (args.Request.Headers.Contains("Authorization"))
                        {
                            req.Headers.Add("Authorization", args.Request.Headers.GetHeader("Authorization"));
                        }

                        var resp = await _httpClient.SendAsync(req);
                        var stream = await resp.Content.ReadAsStreamAsync();
                        var headers = "Access-Control-Allow-Origin: *\r\nAccess-Control-Allow-Headers: *\r\nAccess-Control-Allow-Methods: *\r\nContent-Type: application/json; charset=utf-8\r\n";
                        args.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(stream, (int)resp.StatusCode, resp.ReasonPhrase, headers);
                    }
                    catch
                    {
                    }
                    finally
                    {
                        deferral.Complete();
                    }
                };

                // Handle IPC messages from HTML client (Discord RPC, Auth, etc.)
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
                            if (type == "DISCORD_RPC")
                            {
                                if (root.TryGetProperty("action", out var actProp) && actProp.GetString() == "CLEAR")
                                {
                                    _discordRpc?.ClearActivity();
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
                                webView.CoreWebView2.CookieManager.DeleteAllCookies();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("WebMessage error: " + ex.Message);
                    }
                };

                var appDir = AppDomain.CurrentDomain.BaseDirectory;
                webView.CoreWebView2.SetVirtualHostNameToFolderMapping("soundclode.local", appDir, CoreWebView2HostResourceAccessKind.Allow);
                webView.Source = new Uri("https://soundclode.local/index.html");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка запуска SoundClode: " + ex.Message);
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
                        var options = new CoreWebView2EnvironmentOptions("--disable-web-security --allow-running-insecure-content");
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
                    authWebView.CoreWebView2.Settings.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";

                    // Handle OAuth popups (Google, Apple, Facebook) into a proper separate popup window
                    authWebView.CoreWebView2.NewWindowRequested += async (sender, args) =>
                    {
                        var deferral = args.GetDeferral();
                        try
                        {
                            var popupWin = new Window
                            {
                                Title = "SoundClode — Google / Apple Авторизация",
                                Width = 520,
                                Height = 680,
                                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                                Owner = authWin,
                                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White),
                                ResizeMode = ResizeMode.CanResize
                            };
                            currentPopup = popupWin;

                            var popupWv = new Microsoft.Web.WebView2.Wpf.WebView2();
                            popupWin.Content = popupWv;

                            popupWin.Show();

                            await popupWv.EnsureCoreWebView2Async(_env);

                            popupWv.CoreWebView2.Settings.IsStatusBarEnabled = false;
                            popupWv.CoreWebView2.Settings.AreDevToolsEnabled = false;
                            popupWv.CoreWebView2.Settings.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";

                            popupWv.CoreWebView2.WindowCloseRequested += (s, e) =>
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    try { popupWin.Close(); } catch { }
                                });
                            };

                            args.NewWindow = popupWv.CoreWebView2;
                            args.Handled = true;
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

                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
                    timer.Tick += async (ts, te) =>
                    {
                        try
                        {
                            var cookies = await authWebView.CoreWebView2.CookieManager.GetCookiesAsync("https://soundcloud.com");
                            if (cookies == null || cookies.Count == 0)
                            {
                                cookies = await authWebView.CoreWebView2.CookieManager.GetCookiesAsync("https://secure.soundcloud.com");
                            }

                            string? oauthToken = null;
                            foreach (var c in cookies)
                            {
                                if (c.Name == "oauth_token")
                                {
                                    oauthToken = c.Value;
                                    break;
                                }
                            }

                            if (!string.IsNullOrEmpty(oauthToken))
                            {
                                timer.Stop();

                                using var req = new HttpRequestMessage(HttpMethod.Get, "https://api-v2.soundcloud.com/me");
                                req.Headers.Add("Authorization", "OAuth " + oauthToken);
                                req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
                                var resp = await _httpClient.SendAsync(req);
                                if (resp.IsSuccessStatusCode)
                                {
                                    var userJson = await resp.Content.ReadAsStringAsync();
                                    webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
                                      {
                                          type = "AUTH_SUCCESS",
                                          token = oauthToken,
                                          user = JsonSerializer.Deserialize<JsonElement>(userJson)
                                      }));
                                    try { currentPopup?.Close(); } catch { }
                                    authWin.Close();
                                }
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

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void BtnMaximize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = (WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;
        }
        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
