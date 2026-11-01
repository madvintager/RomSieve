using RomSieve.App.Abstractions;
using RomSieve.App.ViewModels;
using RomSieve.Core.Models;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.App.Tests;

/// <summary>
/// Orders of the 1G1R criteria: the order of the effective convention is shown, and the criteria that cannot separate
/// two releases of the collection being sorted (the parent / clone relations of its DAT, the Amiga it is sorted for,
/// a preference left empty) are greyed out, with the reason in their tooltip.
/// </summary>
public sealed class TiebreakApplicabilityViewModelTests : IDisposable
{
    private readonly TempDirectoryFixture _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private MainViewModel CreateViewModel() =>
        TestMainViewModel.Create(new HeadlessDialogService(), _tmp.GetFilePath("config.json"));

    private static TiebreakCriterionItemViewModel Item(TiebreakOrderViewModel order, TiebreakCriterion criterion) =>
        order.Items.Single(i => i.Criterion == criterion);

    private static IEnumerable<TiebreakCriterion> GreyedOut(TiebreakOrderViewModel order) =>
        order.Items.Where(i => i.HasNoEffect).Select(i => i.Criterion);

    private string WriteNoIntroDat() => _tmp.WriteText("snes.dat", """
        <?xml version="1.0"?>
        <datafile>
        	<header><name>Nintendo - Super Nintendo Entertainment System (Parent-Clone)</name><homepage>No-Intro</homepage></header>
        	<game name="Super Mario World (USA)"><description>Super Mario World (USA)</description><rom name="Super Mario World (USA).sfc" size="524288" crc="b19ed489"/></game>
        	<game name="Super Mario World (Europe) (Rev 1)" cloneof="Super Mario World (USA)"><description>Super Mario World (Europe) (Rev 1)</description><rom name="Super Mario World (Europe) (Rev 1).sfc" size="524288" crc="5ff5a5b3"/></game>
        </datafile>
        """);

    private string WriteTosecDat() => _tmp.WriteText("tosec.dat", """
        clrmamepro (
        	name "Commodore Amiga - Games (TOSEC)"
        	version "2024-01-01"
        )
        game (
        	name "Xenon 2 (1989)(Mirrorsoft)"
        	rom ( name "Xenon 2 (1989)(Mirrorsoft).adf" size 901120 crc 11223344 )
        )
        """);

    [Fact]
    public void WithoutDat_TheParentCloneCriterionAndThoseWithoutPreferenceShouldBeGreyedOut()
    {
        var vm = CreateViewModel();
        var noIntro = vm.Priorities.NoIntroCriteriaOrder;
        var tosec = vm.Priorities.TosecCriteriaOrder;

        GreyedOut(noIntro).Should().Equal(TiebreakCriterion.VideoStandard, TiebreakCriterion.ParentClone);
        noIntro.HasCriteriaWithoutEffect.Should().BeTrue();
        Item(noIntro, TiebreakCriterion.ParentClone).NoEffectText.Should().Be(vm.I18n["tiebreak.no_effect_no_dat"]);
        Item(noIntro, TiebreakCriterion.VideoStandard).NoEffectText.Should().Be(vm.I18n["tiebreak.no_effect_no_preference"]);
        Item(noIntro, TiebreakCriterion.Version).NoEffectText.Should().BeNull();

        GreyedOut(tosec).Should().Equal(TiebreakCriterion.AmigaChipset, TiebreakCriterion.VideoStandard, TiebreakCriterion.PreferredMediaType);
        Item(tosec, TiebreakCriterion.AmigaChipset).NoEffectText.Should().Be(vm.I18n["tiebreak.no_effect_amiga_target"]);
    }

    [Fact]
    public async Task NoIntroDatWithClones_ShouldShowTheOrderOfNoIntroWithoutTheCriteriaOfOtherConventions()
    {
        var vm = CreateViewModel();
        vm.Priorities.PreferredVideo = VideoPreference.Pal;
        vm.Directories.DatFilePath = WriteNoIntroDat();
        await vm.Directories.DatLoadTask;

        vm.Directories.EffectiveConvention.Should().Be(NamingConvention.NoIntro);
        var order = vm.Priorities.ShownCriteriaOrder;
        order.Should().BeSameAs(vm.Priorities.NoIntroCriteriaOrder);
        GreyedOut(order).Should().BeEmpty();
        order.Order.Should().NotContain(
            [TiebreakCriterion.AmigaChipset, TiebreakCriterion.DriverStatus, TiebreakCriterion.PreferredMediaType, TiebreakCriterion.Date]);
    }

    [Fact]
    public async Task TosecDatWithoutClones_ShouldShowTheOrderOfTosec()
    {
        var vm = CreateViewModel();
        vm.Priorities.PreferredVideo = VideoPreference.Pal;
        vm.TosecFilters.PreferredMediaType = MediaPreference.Floppy;
        vm.Directories.DatFilePath = WriteTosecDat();
        await vm.Directories.DatLoadTask;

        vm.Directories.EffectiveConvention.Should().Be(NamingConvention.Tosec);
        var order = vm.Priorities.ShownCriteriaOrder;
        order.Should().BeSameAs(vm.Priorities.TosecCriteriaOrder);
        GreyedOut(order).Should().Equal(TiebreakCriterion.AmigaChipset);
        Item(vm.Priorities.NoIntroCriteriaOrder, TiebreakCriterion.ParentClone).NoEffectText.Should().Be(vm.I18n["tiebreak.no_effect_no_clones"]);
    }

    [Fact]
    public void ParentClone_ShouldOnlyBeGreyedOutOnceTheWholeDatSaysItHasNoClones()
    {
        var services = new CoreTestServices();
        var directories = new DirectoriesViewModel(services.I18n, new HeadlessDialogService(), services.DatService, services.NoIntroParser, services.Organizer, services.MameParser, TimeSpan.Zero);
        directories.DatCloneRelations.Should().Be(DatCloneRelations.NoDat);

        directories.DatFilePath = _tmp.GetFilePath("missing.dat");
        directories.DatCloneRelations.Should().Be(DatCloneRelations.Unknown, "nothing is claimed about a DAT that cannot be read");

        directories.DatFilePath = WriteNoIntroDat();
        directories.DatCloneRelations.Should().Be(DatCloneRelations.Present, "the first games of the DAT have a clone");

        directories.DatFilePath = WriteTosecDat();
        directories.DatCloneRelations.Should().Be(DatCloneRelations.Unknown, "the first games without a clone prove nothing");
    }

    [Fact]
    public async Task ParentClone_ShouldBeKnownFromTheWholeDat()
    {
        var services = new CoreTestServices();
        var directories = new DirectoriesViewModel(services.I18n, new HeadlessDialogService(), services.DatService, services.NoIntroParser, services.Organizer, services.MameParser, TimeSpan.Zero);

        directories.DatFilePath = WriteTosecDat();
        await directories.DatLoadTask;

        directories.DatCloneRelations.Should().Be(DatCloneRelations.None);
    }

    [Fact]
    public void MameOrder_ShouldHaveNoCriterionWithoutEffectWithClones()
    {
        var vm = CreateViewModel();

        vm.Priorities.ShowConvention(NamingConvention.Mame, DatCloneRelations.Present);

        var order = vm.Priorities.ShownCriteriaOrder;
        order.Should().BeSameAs(vm.Priorities.MameCriteriaOrder);
        GreyedOut(order).Should().BeEmpty("a board is neither PAL nor NTSC: the MAME order has no video standard");
    }

    [Fact]
    public void GreyedOutCriteria_ShouldStayGreyedOutWhenTheOrderChanges()
    {
        var vm = CreateViewModel();
        vm.Priorities.ShowConvention(NamingConvention.NoIntro, DatCloneRelations.NoDat);
        var order = vm.Priorities.NoIntroCriteriaOrder;
        var itemsBefore = order.Items.Select(i => i.Criterion).ToList();

        order.SelectedItem = Item(order, TiebreakCriterion.ParentClone);
        order.MoveUp();

        order.Items.Select(i => i.Criterion).Should().NotEqual(itemsBefore);
        Item(order, TiebreakCriterion.ParentClone).HasNoEffect.Should().BeTrue();
        GreyedOut(order).Should().HaveCount(2);
    }

    [Fact]
    public void LanguageChange_ShouldTranslateTheReasons()
    {
        var vm = CreateViewModel();
        vm.UiLocale = "fr";
        vm.Priorities.ShowConvention(NamingConvention.Redump, DatCloneRelations.None);
        Item(vm.Priorities.RedumpCriteriaOrder, TiebreakCriterion.ParentClone).NoEffectText.Should().Contain("aucune relation parent / clone");

        vm.UiLocale = "en";

        Item(vm.Priorities.RedumpCriteriaOrder, TiebreakCriterion.ParentClone).NoEffectText
            .Should().Be("No effect on this collection: the DAT describes no parent / clone relation.");
    }

    [Fact]
    public void AutoConvention_ShouldClaimNothingButTheDatRelationsAndThePreferences()
    {
        var vm = CreateViewModel();
        vm.TosecFilters.AmigaTarget = AmigaTarget.Aga;
        vm.Priorities.PreferredVideo = VideoPreference.Pal;
        vm.TosecFilters.PreferredMediaType = MediaPreference.Floppy;

        vm.Priorities.ShowConvention(NamingConvention.Auto, DatCloneRelations.Unknown);

        vm.Priorities.CriteriaOrders.Should().OnlyContain(order => !order.HasCriteriaWithoutEffect, "the files may follow any convention");
    }

    [Theory]
    [InlineData(AmigaTarget.Any, true)]
    [InlineData(AmigaTarget.Ocs, true)]
    [InlineData(AmigaTarget.Ecs, true)]
    [InlineData(AmigaTarget.Aga, false)]
    public void AmigaChipset_ShouldOnlyApplyToTheAgaTarget(AmigaTarget target, bool expectedGreyedOut)
    {
        var vm = CreateViewModel();
        vm.Priorities.ShowConvention(NamingConvention.Tosec, DatCloneRelations.None);

        vm.TosecFilters.AmigaTarget = target;

        Item(vm.Priorities.TosecCriteriaOrder, TiebreakCriterion.AmigaChipset).HasNoEffect.Should().Be(expectedGreyedOut);
    }
}
