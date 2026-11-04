namespace Limona;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        try
        {
            Limona.Interop.Dbg.Log("Program.Main started");

            var languageArgument = Array.FindIndex(args,
                argument => argument.Equals("--set-language", StringComparison.OrdinalIgnoreCase));
            if (languageArgument >= 0)
            {
                if (languageArgument + 1 >= args.Length ||
                    !Limona.Services.Loc.TryParse(args[languageArgument + 1], out var language))
                {
                    Limona.Interop.Dbg.Log("Program.Main: invalid --set-language value");
                    Environment.ExitCode = 2;
                    return;
                }

                try
                {
                    Limona.Services.AppSettings.SetLanguageFromInstaller(language);
                    Limona.Interop.Dbg.Log("Program.Main: installer selected language " +
                        Limona.Services.Loc.Code(language));
                }
                catch (Exception ex)
                {
                    Limona.Interop.Dbg.Log("Program.Main --set-language THREW: " + ex);
                    Environment.ExitCode = 1;
                }
                return;
            }

            global::WinRT.ComWrappersSupport.InitializeComWrappers();
            // An AUMID is required for Shell_NotifyIconW in unpackaged apps
            Limona.Interop.Win32.SetCurrentProcessExplicitAppUserModelID("Limona.App");

            // One-time migration of user data written by the old build's folder
            // name (RadialMenu) into Limona's data folder: settings, caches and
            // downloaded models carry over automatically.
            try
            {
                var oldData = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RadialMenu");
                var newData = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Limona");
                if (System.IO.Directory.Exists(oldData))
                {
                    if (!System.IO.Directory.Exists(newData))
                        System.IO.Directory.Move(oldData, newData);
                    else
                    {
                        // Merge: copy anything the new folder does not have yet.
                        foreach (var path in System.IO.Directory.EnumerateFiles(oldData, "*",
                            System.IO.SearchOption.AllDirectories))
                        {
                            var rel = System.IO.Path.GetRelativePath(oldData, path);
                            var target = System.IO.Path.Combine(newData, rel);
                            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                            if (!System.IO.File.Exists(target))
                                System.IO.File.Copy(path, target);
                        }
                        try { System.IO.Directory.Delete(oldData, recursive: true); } catch { }
                    }
                }
            }
            catch { /* best effort — defaults still work */ }

            // One-file font installer mode: Limona.exe --install-fonts
            if (Array.Exists(args, a => a.Equals("--install-fonts", StringComparison.OrdinalIgnoreCase)))
            {
                var count = Limona.Services.FontPack.EnsureInstalled(force: true);
                Limona.Interop.Dbg.Log($"FontPack: installer mode finished ({count} file(s) copied)");
                return;
            }

            // Must run before Application.Start so DirectWrite sees the families
            // on the very first launch. Registers the bundled fonts for this user.
            try
            {
                Limona.Services.FontPack.EnsureInstalled();
            }
            catch (Exception ex)
            {
                Limona.Interop.Dbg.Log("FontPack THREW: " + ex.Message);
            }

            // Single-instance guard: only the first process continues past this.
            using var mutex = new System.Threading.Mutex(true, "Limona.SingleInstance", out var createdNew);
            if (!createdNew)
            {
                Limona.Interop.Dbg.Log("Program.Main: another instance is running (mutex held)");                return;
            }

            Limona.Interop.Dbg.Log("Program.Main starting Application.Start");
            Microsoft.UI.Xaml.Application.Start(p =>
            {
                var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                    Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                System.Threading.SynchronizationContext.SetSynchronizationContext(context);
                new Limona.App();
            });
            Limona.Interop.Dbg.Log("Program.Main Application.Start RETURNED");
        }
        catch (Exception ex)
        {
            Limona.Interop.Dbg.Log("Program.Main THREW: " + ex.ToString());
            throw;
        }
        finally
        {
            Limona.Interop.Dbg.Log("Program.Main exiting finally block");
        }
    }
}
