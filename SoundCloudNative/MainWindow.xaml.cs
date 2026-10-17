using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace SoundCloudNative
{
    public partial class MainWindow : Window
    {
        private bool _isPinned;
        private static readonly HttpClient _httpClient = new HttpClient();

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
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var userDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundClode", "WebViewProfile");
                var options = new CoreWebView2EnvironmentOptions("--disable-web-security --allow-running-insecure-content");
                var env = await CoreWebView2Environment.CreateAsync(null, userDataDir, options);
                await webView.EnsureCoreWebView2Async(env);

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
                        req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

                        var resp = await _httpClient.SendAsync(req);
                        var stream = await resp.Content.ReadAsStreamAsync();
                        var headers = "Access-Control-Allow-Origin: *\r\nAccess-Control-Allow-Headers: *\r\nContent-Type: application/json; charset=utf-8\r\n";
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

                var appDir = AppDomain.CurrentDomain.BaseDirectory;
                webView.CoreWebView2.SetVirtualHostNameToFolderMapping("soundclode.local", appDir, CoreWebView2HostResourceAccessKind.Allow);
                webView.Source = new Uri("https://soundclode.local/index.html");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка запуска SoundClode: " + ex.Message);
            }
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
