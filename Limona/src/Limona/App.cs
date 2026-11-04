using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Limona.Interop;
using Limona.Menu;

namespace Limona;

/// <summary>
/// Background application shell: wires the hook thread to the UI thread and
/// hosts the overlay window. No main window is ever created at startup.
/// </summary>
public sealed partial class App : Application
{
    private readonly DispatcherQueue _uiQueue;
    private HookService? _hooks;
    private LimonaWindow? _menuWindow;

    public App()
    {
        InitializeComponent();
        this.RequestedTheme = ApplicationTheme.Dark;
        _uiQueue = DispatcherQueue.GetForCurrentThread();

        this.UnhandledException += (s, e) =>
        {
            Limona.Interop.Dbg.Log("XAML UnhandledException: " + e.Exception?.ToString() + " msg=" + e.Message);
        };
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            Limona.Interop.Dbg.Log("AppDomain UnhandledException: " + e.ExceptionObject?.ToString());
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            Limona.Interop.Dbg.Log("TaskScheduler UnobservedTaskException: " + e.Exception?.ToString());
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Limona.Interop.Dbg.Log("App.OnLaunched entered");
        Limona.Services.AppSettings.Load(); // user colors before any window builds
        Limona.Services.AppSettings.EnsureWindowsStartup(); // mandatory: app starts with Windows
        _hooks = new HookService();
        try
        {
            _menuWindow = new LimonaWindow();
            Limona.Interop.Dbg.Log("LimonaWindow created successfully");
        }
        catch (Exception ex)
        {
            Limona.Interop.Dbg.Log("LimonaWindow ctor THREW: " + ex.ToString());
            throw;
        }

        // Always unhook on teardown, even on abrupt shutdown.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Limona.Interop.Dbg.Log("AppDomain.ProcessExit fired! Stack: " + Environment.StackTrace);
            _hooks?.Dispose();
        };

        _menuWindow.Shown += (x, y, w, h) => _hooks.UpdateMenuBounds(x, y, w, h);
        _menuWindow.Hidden += () => _hooks.NotifyMenuClosed();

        // Hook thread -> UI thread marshalling (the hook side only queues work).
        _hooks.OpenRequested += point =>
        {
            Limona.Interop.Dbg.Log($"hook OpenRequested ({point.X},{point.Y})");
            _uiQueue.TryEnqueue(() =>
            {
                var target = Limona.Interop.ExplorerSelection.GetTargetUnderCursor(point.X, point.Y);
                if (!Limona.Menu.MenuCatalog.IsSupported(target.FullPath))
                {
                    Limona.Interop.Dbg.Log($"hook OpenRequested suppressed — unsupported target: '{target.FullPath}'");
                    return;
                }
                _menuWindow.ShowAt(point.X, point.Y, target.DisplayName, target.FullPath);
            });
        };
        _hooks.DismissRequested += () =>
        {
            Limona.Interop.Dbg.Log("hook DismissRequested");
            _uiQueue.TryEnqueue(() => _menuWindow.CloseAnimated());
        };
        _hooks.EscapeRequested += () =>
        {
            Limona.Interop.Dbg.Log("hook EscapeRequested");
            _uiQueue.TryEnqueue(() => _menuWindow.CloseAnimated());
        };
        _hooks.SettingsRequested += () =>
        {
            Limona.Interop.Dbg.Log("hook SettingsRequested");
            // WinUI windows must be created on the UI thread.
            _uiQueue.TryEnqueue(() => SettingsWindow.Show());
        };
        _hooks.HelpRequested += () =>
        {
            Limona.Interop.Dbg.Log("hook HelpRequested");
            _uiQueue.TryEnqueue(() => HelpWindow.Show());
        };
        _hooks.LanguageRequested += language =>
        {
            Limona.Interop.Dbg.Log("hook LanguageRequested " + language);
            _uiQueue.TryEnqueue(() =>
            {
                Limona.Services.AppSettings.SetLanguage(language);
                _menuWindow?.CloseAnimated();
            });
        };
        _hooks.ExitRequested += () => _uiQueue.TryEnqueue(Shutdown);

        _hooks.Start();
        Limona.Interop.Dbg.Log("App.OnLaunched completed, hooks started");

        // The welcome splash would sit on top of the point being probed by the
        // dev-only diagnostics, so skip it in those modes.
        var headless = Environment.GetCommandLineArgs().Contains("--test-target") ||
                       Environment.GetCommandLineArgs().Contains("--test-show");
        if (Limona.Services.AppSettings.ShowWelcomeAtStartup && !headless)
            WelcomeWindow.Show();

        // Dev-only smoke test: `Limona.exe --test-show` opens the menu at the
        // current cursor position, keeps it ~6 s, then closes it (manual testing aid).
        if (Environment.GetCommandLineArgs().Contains("--test-show"))
        {
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(1200);
                var opened = Win32.GetCursorPos(out var cursor);
                _uiQueue.TryEnqueue(() =>
                {
                    if (opened) _menuWindow!.ShowAt(cursor.X, cursor.Y);
                });
                await System.Threading.Tasks.Task.Delay(6000);
                _uiQueue.TryEnqueue(() => _menuWindow!.CloseAnimated());
            });
        }
        var cli = Environment.GetCommandLineArgs();
        // Dev-only smoke test: `Limona.exe --test-settings` opens the
        // settings window after boot, closes it after ~8 s.
        if (Environment.GetCommandLineArgs().Contains("--test-settings"))
        {
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(1200);
                _uiQueue.TryEnqueue(() => Limona.Menu.SettingsWindow.Show());
                await System.Threading.Tasks.Task.Delay(8000);
                // Leave the process running for manual follow-up tests.
            });
        }

        // Dev-only smoke test: `Limona.exe --test-help` opens the Help window
        // after boot (it stays open — manual content check aid).
        if (Environment.GetCommandLineArgs().Contains("--test-help"))
        {
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(1200);
                _uiQueue.TryEnqueue(() => Limona.Menu.HelpWindow.Show());
            });
        }

        // Dev-only probe: `Limona.exe --test-target X Y` logs which file/folder
        // Limona would act on at that screen point (selection-bug regression aid).
        {
            var targs = Environment.GetCommandLineArgs();
            var idx = Array.IndexOf(targs, "--test-target");
            if (idx >= 0 && idx + 2 < targs.Length &&
                int.TryParse(targs[idx + 1], out var tx) && int.TryParse(targs[idx + 2], out var ty))
            {
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(1500);
                    _uiQueue.TryEnqueue(() =>
                    {
                        var t = Limona.Interop.ExplorerSelection.GetTargetUnderCursor(tx, ty);
                        Limona.Interop.Dbg.Log(
                            $"TEST-TARGET ({tx},{ty}) → name='{t.DisplayName}' path='{t.FullPath}' " +
                            $"supported={Limona.Menu.MenuCatalog.IsSupported(t.FullPath)}");
                    });
                });
            }
        }

        // Dev smoke test for all 5 folder customization features
        if (Environment.GetCommandLineArgs().Contains("--test-customization"))
        {
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(1000);
                try
                {
                    var testDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Limona", "TestFolder");
                    System.IO.Directory.CreateDirectory(testDir);

                    var (covBmp, covIco, covPath) = Limona.Services.FolderCustomizationService.GenerateCoverIcon(testDir, new Limona.Services.CoverSettings(null));
                    var (emBmp, emIco) = Limona.Services.FolderCustomizationService.GenerateEmojiIcon(new Limona.Services.EmojiSettings());
                    var (bdgBmp, bdgIco) = Limona.Services.FolderCustomizationService.GenerateBadgeIcon(testDir, new Limona.Services.BadgeSettings());
                    var (mnBmp, mnIco) = Limona.Services.FolderCustomizationService.GenerateMonogramIcon(new Limona.Services.MonogramSettings());
                    var (patBmp, patIco) = Limona.Services.FolderCustomizationService.GeneratePatternIcon(new Limona.Services.PatternSettings());

                    Limona.Interop.Dbg.Log($"TEST-CUSTOMIZATION SUCCESS: Cover={covIco.Length}b, Emoji={emIco.Length}b, Badge={bdgIco.Length}b, Monogram={mnIco.Length}b, Pattern={patIco.Length}b");

                    covBmp.Dispose(); emBmp.Dispose(); bdgBmp.Dispose(); mnBmp.Dispose(); patBmp.Dispose();

                    _uiQueue.TryEnqueue(() =>
                    {
                        Limona.Menu.FolderCustomizationWindow.ShowForFile(testDir, Limona.Menu.FolderCustomTab.Cover);
                    });
                }
                catch (Exception ex)
                {
                    Limona.Interop.Dbg.Log("TEST-CUSTOMIZATION THREW: " + ex);
                }
            });
        }
        var bgIdx = Array.IndexOf(cli, "--test-bg");
        if (bgIdx >= 0 && bgIdx + 1 < cli.Length)
        {
            var testFile = cli[bgIdx + 1];
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(900);
                _uiQueue.TryEnqueue(() =>
                {
                    try { Limona.Menu.BackgroundPreviewWindow.ShowForFile(testFile); }
                    catch (Exception ex) { Limona.Interop.Dbg.Log("TEST bg THREW: " + ex); }
                });
            });
        }
        var bgKeyIdx = Array.IndexOf(cli, "--test-bgkey");
        if (bgKeyIdx >= 0 && bgKeyIdx + 1 < cli.Length)
        {
            var testFile = cli[bgKeyIdx + 1];
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var (pixels, w, h, _, _) = await Limona.Services.ImageConverter.DecodeScaledPixelsAsync(testFile, 900);
                    var keyColor = ((byte)200, (byte)40, (byte)35);
                    var keyed = Limona.Services.BackgroundRemover.DetectBackground(pixels, w, h, 60, keyColor, false);
                    var count = keyed?.Count(b => b > 127) ?? 0;
                    Limona.Interop.Dbg.Log($"TEST-BGKEY keyed={count}/{w * h} ({(100.0 * count / (w * h)):F1}%)");

                    var baseAlpha = new byte[w * h];
                    Array.Fill(baseAlpha, (byte)255);
                    var outPixels = (byte[])pixels.Clone();
                    Limona.Services.BackgroundRemover.ApplyCompose(outPixels, baseAlpha, null, w, h, keyed);
                    var png = await Limona.Services.ImageConverter.EncodePngBytesAsync(outPixels, w, h);
                    var dest = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(testFile)!, "bgkey_result.png");
                    System.IO.File.WriteAllBytes(dest, png);
                    Limona.Interop.Dbg.Log("TEST-BGKEY wrote " + dest);
                }
                catch (Exception ex)
                {
                    Limona.Interop.Dbg.Log("TEST-BGKEY THREW: " + ex);
                }
            });
        }
        var editIdx = Array.IndexOf(cli, "--test-edit");
        if (editIdx >= 0 && editIdx + 1 < cli.Length)
        {
            var editFile = cli[editIdx + 1];
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(900);
                _uiQueue.TryEnqueue(() =>
                {
                    try { Limona.Menu.ImageEditWindow.ShowForFile(editFile); }
                    catch (Exception ex) { Limona.Interop.Dbg.Log("TEST edit THREW: " + ex); }
                });
            });
        }
        var trimIdx = Array.IndexOf(cli, "--test-trimui");
        if (trimIdx >= 0 && trimIdx + 1 < cli.Length)
        {
            var testFile = cli[trimIdx + 1];
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(900);
                _uiQueue.TryEnqueue(() =>
                {
                    try { Limona.Menu.VideoTrimWindow.ShowForFile(testFile); }
                    catch (Exception ex) { Limona.Interop.Dbg.Log("TEST trimui THREW: " + ex); }
                });
                await System.Threading.Tasks.Task.Delay(45000);
                _uiQueue.TryEnqueue(Shutdown);
            });
        }
        var benchIdx = Array.IndexOf(cli, "--benchexport");
        if (benchIdx >= 0 && benchIdx + 1 < cli.Length)
        {
            var benchFile = cli[benchIdx + 1];
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(1200);
                try
                {
                    var edit = new Limona.Services.EditSpec();
                    edit.Texts.Add(new Limona.Services.TextOverlay
                    {
                        Text = "Benchmark 123",
                        XFraction = 0.5,
                        YFraction = 0.5,
                    });
                    edit.VideoVolume = 0.4;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var outPath = await Limona.Services.MediaFoundationVideo.CropToMp4Async(
                        benchFile, 0.35, 0.045, 0.50, 0.925, null,
                        msg => Limona.Interop.Dbg.Log("BENCH status: " + msg),
                        TimeSpan.Zero, TimeSpan.FromSeconds(10), edit);
                    sw.Stop();
                    Limona.Interop.Dbg.Log($"BENCH done in {sw.Elapsed.TotalSeconds:0.##} s -> {outPath} ({new FileInfo(outPath).Length} bytes)");
                }
                catch (Exception ex) { Limona.Interop.Dbg.Log("BENCH THREW: " + ex); }
                await System.Threading.Tasks.Task.Delay(500);
                _uiQueue.TryEnqueue(Shutdown);
            });
        }
    }

    private void Shutdown()
    {
        // Uninstall hooks first so no input is misrouted during teardown.
        _hooks?.Dispose();
        _menuWindow?.Close();

        // WinUI has no managed Application.Exit: terminate the background process.
        Environment.Exit(0);
    }
}
