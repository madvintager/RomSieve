using Avalonia;
using Avalonia.Headless;

namespace RomSieve.App.Tests;

/// <summary>
/// The Avalonia Headless session of the UI tests, started by the first of them.
/// </summary>
/// <remarks>
/// One application serves every test (<see cref="AvaloniaTestIsolationLevel.PerAssembly"/>). With the default
/// isolation (<see cref="AvaloniaTestIsolationLevel.PerTest"/>), the end of each dispatch disposes the font manager of
/// its application, while work left by a test (a debounce of the pattern preview, a continuation, a dispatcher job)
/// may still ask for <c>FontManager.Current</c> once no test scope is active: the font manager is then registered in
/// the root scope shared by every test, and disposed by the end of the next test, so that every later window fails on
/// <c>'fonts:SystemFonts'</c> (and native text resources may be used after being freed). With one application,
/// nothing is disposed between tests. The UI dispatcher is then never reset either: no code may read
/// <c>Dispatcher.UIThread</c> before the first UI test sets up the application (see <c>ExecutionViewModel</c>).
/// </remarks>
internal static class HeadlessTestSession
{
    public sealed class AppBuilderEntryPoint
    {
        // ROMSIEVE_HEADLESS_SKIA=1 renders with Skia, so that windows can be captured as images (theme screenshots)
        public static AppBuilder BuildAvaloniaApp() =>
            Environment.GetEnvironmentVariable("ROMSIEVE_HEADLESS_SKIA") == "1"
                ? AppBuilder.Configure<RomSieve.App.App>()
                    .UseSkia()
                    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                : AppBuilder.Configure<RomSieve.App.App>()
                    .UseHeadless(new AvaloniaHeadlessPlatformOptions
                    {
                        UseHeadlessDrawing = true
                    });
    }

    private static readonly Lazy<HeadlessUnitTestSession> LazySession = new(
        () => HeadlessUnitTestSession.StartNew(typeof(AppBuilderEntryPoint), AvaloniaTestIsolationLevel.PerAssembly));

    public static HeadlessUnitTestSession Session => LazySession.Value;
}
