using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Screenbox;

public sealed partial class App : Application
{
    private const string Win2DBugHandlingStateFileName = "win2d-bug-handling-state.json";
    private static string Win2DBugHandlingStateFilePath =>
        Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, Win2DBugHandlingStateFileName);

    private static bool _suppressGlobalCrashHandling;

    public App()
    {
        this.InitializeComponent();
        this.UnhandledException += OnUnhandledException;
    }

    private static void OnUnhandledException(object sender, Windows.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        if (_suppressGlobalCrashHandling) return;
        if (IsWin2DBug(e.Exception))
        {
            // Workaround for Win2D bug with Windows App SDK 1.8: https://github.com/microsoft/Win2D/issues/951
            e.Handled = true;
            HandleWin2DBugAsync().Track();
        }
    }

    private static bool IsWin2DBug(Exception ex)
    {
        return ex is DllNotFoundException or TypeInitializationException or Exception { HResult: -2147467259 };
    }

    private static async Task HandleWin2DBugAsync()
    {
        // …
    }
}
