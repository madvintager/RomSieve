using FluentAssertions;
using RomSieve.App;
using RomSieve.App.Abstractions;
using RomSieve.App.ViewModels;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using Xunit;

namespace RomSieve.App.Tests;

public sealed class UpdateCheckerViewModelTests
{
    private const string ReleaseUrl = "https://github.com/madvintager/romsieve/releases/tag/v1.2.0";

    private static readonly UpdateInfo Release120 = new("v1.2.0", "1.2.0", ReleaseUrl);

    private sealed class FakeUpdateCheckService : IUpdateCheckService
    {
        public int CallCount { get; private set; }
        public UpdateCheckResult ResultToReturn { get; set; } = UpdateCheckResult.UpToDate("1.0.0");

        public Task<UpdateCheckResult> CheckForUpdatesAsync(string? currentVersion = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(ResultToReturn);
        }
    }

    private sealed class Harness
    {
        public FakeUpdateCheckService Service { get; } = new();
        public HeadlessDialogService Dialogs { get; } = new();
        public int SaveCount { get; private set; }
        public UpdateCheckerViewModel ViewModel { get; }

        public Harness(string currentVersion = "1.0.0")
        {
            ViewModel = new UpdateCheckerViewModel(
                Service,
                Dialogs,
                new LocalizationService("fr"),
                () =>
                {
                    SaveCount++;
                    return Task.CompletedTask;
                },
                currentVersion);
        }
    }

    [Fact]
    public void ShouldCheckAtStartup_WhenDisabled_ShouldBeFalse()
    {
        var harness = new Harness();
        harness.ViewModel.CheckOnStartup = false;

        harness.ViewModel.ShouldCheckAtStartup(DateTimeOffset.UtcNow).Should().BeFalse();
    }

    [Fact]
    public void ShouldCheckAtStartup_InDevelopmentBuild_ShouldBeFalse()
    {
        new Harness("0.0.0-dev").ViewModel.ShouldCheckAtStartup(DateTimeOffset.UtcNow).Should().BeFalse();
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("not a date", true)]
    [InlineData(-2.0, false)]
    [InlineData(-25.0, true)]
    public void ShouldCheckAtStartup_ShouldRunAtMostOnceADay(object? lastCheck, bool expected)
    {
        var now = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
        var harness = new Harness();
        harness.ViewModel.LastCheckUtc = lastCheck is double hours ? now.AddHours(hours).ToString("O") : (string?)lastCheck;

        harness.ViewModel.ShouldCheckAtStartup(now).Should().Be(expected);
    }

    [Fact]
    public void TriggerStartupCheck_WhenNotAllowed_ShouldNotCallService()
    {
        var harness = new Harness();
        harness.ViewModel.CheckOnStartup = false;

        harness.ViewModel.TriggerStartupCheck();

        harness.Service.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task StartupCheck_WhenUpdateAvailable_ShouldShowNotificationAndSave()
    {
        var harness = new Harness();
        harness.Service.ResultToReturn = UpdateCheckResult.Available("1.0.0", Release120);

        await harness.ViewModel.RunStartupCheckAsync();

        harness.ViewModel.IsNotificationVisible.Should().BeTrue();
        harness.ViewModel.LatestVersion.Should().Be("1.2.0");
        harness.ViewModel.NotificationMessage.Should().Contain("v1.2.0");
        harness.ViewModel.LastCheckUtc.Should().NotBeNull();
        harness.SaveCount.Should().Be(1);
        harness.Dialogs.ShownMessages.Should().BeEmpty();
    }

    [Theory]
    [InlineData("1.2.0")]
    [InlineData("v1.2.0")]
    public async Task StartupCheck_WhenVersionIgnored_ShouldNotShowNotification(string ignored)
    {
        var harness = new Harness();
        harness.Service.ResultToReturn = UpdateCheckResult.Available("1.0.0", Release120);
        harness.ViewModel.IgnoredVersion = ignored;

        await harness.ViewModel.RunStartupCheckAsync();

        harness.ViewModel.IsNotificationVisible.Should().BeFalse();
    }

    [Fact]
    public async Task StartupCheck_WhenFailed_ShouldStaySilent()
    {
        var harness = new Harness();
        harness.Service.ResultToReturn = UpdateCheckResult.Failed("1.0.0", LocalizedMessage.Create("update.error_http", ("status", 403)));

        await harness.ViewModel.RunStartupCheckAsync();

        harness.ViewModel.IsNotificationVisible.Should().BeFalse();
        harness.Dialogs.ShownMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task ManualCheck_WhenUpToDate_ShouldShowInfoDialog()
    {
        var harness = new Harness();

        await harness.ViewModel.CheckForUpdatesAsync();

        harness.Service.CallCount.Should().Be(1);
        harness.ViewModel.IsNotificationVisible.Should().BeFalse();
        harness.Dialogs.ShownMessages.Should().ContainSingle(m => m.Level == "INFO");
        harness.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task ManualCheck_WhenError_ShouldShowWarningDialog()
    {
        var harness = new Harness();
        harness.Service.ResultToReturn = UpdateCheckResult.Failed("1.0.0", LocalizedMessage.Create("update.error_http", ("status", 403)));

        await harness.ViewModel.CheckForUpdatesAsync();

        harness.ViewModel.IsNotificationVisible.Should().BeFalse();
        harness.Dialogs.ShownMessages.Should().ContainSingle(m => m.Level == "WARN" && m.Message.Contains("403"));
    }

    [Fact]
    public async Task ManualCheck_ShouldShowAnIgnoredVersion()
    {
        var harness = new Harness();
        harness.Service.ResultToReturn = UpdateCheckResult.Available("1.0.0", Release120);
        harness.ViewModel.IgnoredVersion = "1.2.0";

        await harness.ViewModel.CheckForUpdatesAsync();

        harness.ViewModel.IsNotificationVisible.Should().BeTrue();
    }

    [Fact]
    public async Task DismissAndIgnore_ShouldHideTheBannerAndOnlyIgnoreRemembersTheVersion()
    {
        var harness = new Harness();
        harness.Service.ResultToReturn = UpdateCheckResult.Available("1.0.0", Release120);
        await harness.ViewModel.RunStartupCheckAsync();

        harness.ViewModel.DismissNotification();
        harness.ViewModel.IsNotificationVisible.Should().BeFalse();
        harness.ViewModel.IgnoredVersion.Should().BeNull();

        harness.ViewModel.IsNotificationVisible = true;
        await harness.ViewModel.IgnoreVersionAsync();

        harness.ViewModel.IsNotificationVisible.Should().BeFalse();
        harness.ViewModel.IgnoredVersion.Should().Be("1.2.0");
        harness.SaveCount.Should().Be(2);
    }

    [Fact]
    public async Task OpenReleasePage_ShouldOpenTheReleaseOrElseTheReleasesPage()
    {
        var harness = new Harness();

        await harness.ViewModel.OpenReleasePageAsync();
        harness.Service.ResultToReturn = UpdateCheckResult.Available("1.0.0", Release120);
        await harness.ViewModel.RunStartupCheckAsync();
        await harness.ViewModel.OpenReleasePageAsync();

        harness.Dialogs.OpenedUrls.Should().Equal("https://github.com/madvintager/romsieve/releases", ReleaseUrl);
    }

    [Theory]
    [InlineData("https://github.com/madvintager/romsieve/releases/tag/v1.2.0", true)]
    [InlineData("https://GitHub.com/madvintager/romsieve/releases", true)]
    [InlineData("http://github.com/madvintager/romsieve/releases", false)]
    [InlineData("https://github.com.evil.example/releases", false)]
    [InlineData("https://user@github.com/releases", false)]
    [InlineData("https://github.com:8443/releases", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("calc.exe", false)]
    [InlineData(null, false)]
    public void IsTrustedReleaseUrl_ShouldOnlyAcceptHttpsPagesOfGitHub(string? url, bool expected)
    {
        UpdateCheckerViewModel.IsTrustedReleaseUrl(url).Should().Be(expected);
    }

    [Fact]
    public async Task OpenReleasePage_WhenReleaseUrlIsNotTrusted_ShouldOpenTheReleasesPage()
    {
        var harness = new Harness();
        harness.Service.ResultToReturn = UpdateCheckResult.Available(
            "1.0.0", new UpdateInfo("v1.2.0", "1.2.0", "https://evil.example/setup.exe"));
        await harness.ViewModel.RunStartupCheckAsync();

        await harness.ViewModel.OpenReleasePageAsync();

        harness.Dialogs.OpenedUrls.Should().Equal("https://github.com/madvintager/romsieve/releases");
    }

    [Fact]
    public async Task ManualCheck_ShouldTranslateTheErrorInTheInterfaceLanguage()
    {
        var harness = new Harness();
        harness.Service.ResultToReturn = UpdateCheckResult.Failed("1.0.0", LocalizedMessage.Create("update.error_timeout"));

        await harness.ViewModel.CheckForUpdatesAsync();

        harness.Dialogs.ShownMessages.Should().ContainSingle()
            .Which.Message.Should().Be("Impossible de vérifier les mises à jour : GitHub n'a pas répondu à temps.");
    }

    [Fact]
    public async Task NotificationMessage_ShouldFollowTheInterfaceLanguage()
    {
        using var tmp = new TempDirectoryFixture();
        var fake = new FakeUpdateCheckService { ResultToReturn = UpdateCheckResult.Available("1.0.0", Release120) };
        var vm = TestMainViewModel.Create(
            new HeadlessDialogService(),
            tmp.WriteText("config.json", "{}"),
            fake);
        vm.UiLocale = "fr";
        await vm.Updates.RunStartupCheckAsync();
        var french = vm.Updates.NotificationMessage;

        var notified = new List<string?>();
        vm.Updates.PropertyChanged += (_, e) => notified.Add(e.PropertyName);
        vm.UiLocale = "en";

        vm.Updates.NotificationMessage.Should().NotBe(french).And.Contain("v1.2.0");
        notified.Should().Contain(nameof(UpdateCheckerViewModel.NotificationMessage));
    }

    [Fact]
    public async Task Options_ShouldBePersistedInTheConfigurationFile()
    {
        using var tmp = new TempDirectoryFixture();
        var configPath = tmp.WriteText("config.json", """
            {
              "check_updates_on_startup": false,
              "last_update_check_utc": "2026-10-06T12:00:00Z",
              "ignored_update_version": "1.5.0"
            }
            """);

        var vm = TestMainViewModel.Create(
            new HeadlessDialogService(),
            configPath,
            new FakeUpdateCheckService());

        vm.Updates.CheckOnStartup.Should().BeFalse();
        vm.Updates.LastCheckUtc.Should().Be("2026-10-06T12:00:00Z");
        vm.Updates.IgnoredVersion.Should().Be("1.5.0");

        vm.Updates.CheckOnStartup = true;
        vm.Updates.IgnoredVersion = "1.6.0";
        await vm.SaveConfigurationAsync();

        var reloaded = TestMainViewModel.Create(
            new HeadlessDialogService(),
            configPath,
            new FakeUpdateCheckService());

        reloaded.Updates.CheckOnStartup.Should().BeTrue();
        reloaded.Updates.IgnoredVersion.Should().Be("1.6.0");
    }

    [Fact]
    public async Task IgnoreVersion_ShouldSaveTheConfigurationFileInUse()
    {
        using var tmp = new TempDirectoryFixture();
        var configPath = tmp.WriteText("config.json", "{}");
        var fake = new FakeUpdateCheckService { ResultToReturn = UpdateCheckResult.Available("1.0.0", Release120) };
        var vm = TestMainViewModel.Create(new HeadlessDialogService(), configPath, fake);

        await vm.Updates.RunStartupCheckAsync();
        await vm.Updates.IgnoreVersionAsync();

        File.ReadAllText(configPath).Should().Contain("\"ignored_update_version\"").And.Contain("1.2.0");
    }
}
