using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DirectPackageInstaller.Services;
using DirectPackageInstaller.ViewModels;

namespace DirectPackageInstaller.Views
{
    public partial class ConsolePage : UserControl
    {
        ConsoleViewModel Model => (ConsoleViewModel)DataContext!;

        public ConsolePage()
        {
            InitializeComponent();
            // phones / narrow windows: pages restyle through the "narrow" class;
            // the search box gets a line of its own (it sits in the chip WrapPanel)
            NarrowLayout.Watch(this, _ => FitSearch());
            Filters.SizeChanged += (_, _) => FitSearch();
        }

        void FitSearch()
        {
            bool Narrow = Classes.Contains("narrow");
            NarrowLayout.Set(SearchBox, WidthProperty, Math.Max(100, Filters.Bounds.Width - SearchBox.Margin.Right), Narrow && Filters.Bounds.Width > 0);
        }

        /// <summary>Called by the window once the library exists (to show "N PKGs in library").</summary>
        public void Attach(Func<IReadOnlyList<LibraryEntry>> LibraryEntries)
        {
            DataContext = new ConsoleViewModel(LibraryEntries);

            BtnRefresh.Click += async (_, _) => await Model.RefreshAsync();
            BtnCaptures.Click += async (_, _) => await Model.CopyCapturesAsync();
            BtnPatchClose.Click += (_, _) => PatchPanel.IsVisible = false;
            BtnPatchSave.Click += async (_, _) => await SavePatchesAsync();
            BtnRefreshIntro.Click += async (_, _) => await Model.RefreshAsync();

            FilterAll.IsCheckedChanged += (_, _) => { if (FilterAll.IsChecked == true) Model.Filter = ConsoleFilter.All; };
            FilterGames.IsCheckedChanged += (_, _) => { if (FilterGames.IsChecked == true) Model.Filter = ConsoleFilter.Games; };
            FilterApps.IsCheckedChanged += (_, _) => { if (FilterApps.IsChecked == true) Model.Filter = ConsoleFilter.Apps; };
            FilterExt.IsCheckedChanged += (_, _) => { if (FilterExt.IsChecked == true) Model.Filter = ConsoleFilter.Extended; };
        }

        /// <summary>Read the console the first time the page is shown.</summary>
        public async void OnShown()
        {
            if (DataContext is ConsoleViewModel { Loaded: false, IsLoading: false } && !string.IsNullOrWhiteSpace(App.Config.PSIP))
                await Model.RefreshAsync();
        }

        static ConsoleTitleItem? ItemOf(object? Sender) => (Sender as Control)?.Tag as ConsoleTitleItem;

        /// <summary>Uninstalling needs Remote Package Installer; "Show in Library" needs PKGs of the title.</summary>
        void TileMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (sender is not ContextMenu Menu)
                return;

            foreach (var Entry in System.Linq.Enumerable.OfType<MenuItem>(Menu.Items))
            {
                if (Entry.Classes.Contains("rpi"))
                {
                    Entry.IsEnabled = ConsoleStatus.Instance.HasRpi;
                    ToolTip.SetTip(Entry, ConsoleStatus.Instance.HasRpi ? null : "Needs Remote Package Installer open on the console");
                    ToolTip.SetShowOnDisabled(Entry, true);
                }
                if (Entry.Classes.Contains("move"))
                {
                    var Why = Entry.Tag is ConsoleTitleItem { Title.Installing: true } ? "Still installing on the console" : ConsoleMove.WhyNot((Entry.Tag as ConsoleTitleItem)?.TitleId);
                    Entry.IsEnabled = Why == null;
                    ToolTip.SetTip(Entry, Why ?? "The console copies it to the other drive and removes the old copy. Nothing is sent from this device.");
                    ToolTip.SetShowOnDisabled(Entry, true);
                }
                if (Entry.Classes.Contains("ftp"))
                {
                    Entry.IsEnabled = Model.CanCopyCaptures;
                    ToolTip.SetTip(Entry, Model.CanCopyCaptures
                        ? $"Copies this game's saved data for every user to {ConsoleSaves.Folder} (read-only on the console)"
                        : ConsoleStatus.Instance.FtpOpen ? "Another copy is running" : "Needs GoldHEN's FTP server running on the console");
                    ToolTip.SetShowOnDisabled(Entry, true);
                }
                if (Entry.Classes.Contains("library"))
                    Entry.IsEnabled = Entry.Tag is ConsoleTitleItem Item && Item.LibraryPackages > 0;
            }
        }

        void MenuShowInLibraryClick(object? sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is { } Item)
                AppShell.Current?.ShowInLibrary(Item.TitleId);
        }

        async void MenuCopyIdClick(object? sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is { } Item && TopLevel.GetTopLevel(this)?.Clipboard is { } Clipboard)
                await Clipboard.SetTextAsync(Item.TitleId);
        }

        void MenuUninstallClick(object? sender, RoutedEventArgs e) => Uninstall(ItemOf(sender), "gd");

        // ----- game patches panel

        string PatchTitleId = "";
        byte[]? PatchXml;
        List<GamePatch> Patches = new();
        bool PatchBusy;

        async void MenuPatchesClick(object? sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is not { } Item || string.IsNullOrWhiteSpace(App.Config.PSIP))
                return;

            PatchTitleId = Item.TitleId;
            PatchXml = null;
            Patches = new();
            PatchList.ItemsSource = null;
            PatchHeading.Text = $"{Item.Name}  ·  v{Item.Title.Version}";
            PatchFooter.Text = "";
            SetPatchStatus("Loading the patch database…");
            PatchPanel.IsVisible = true;
            SyncPatchSave();

            try
            {
                await GamePatches.DatabaseAsync();
                PatchXml = GamePatches.XmlFor(Item.TitleId);
                var Version = GamePatches.DatabaseVersion();
                PatchFooter.Text = "Patches from the PS-Game-Patch database (github.com/illusionyy/PS-Game-Patch)"
                                   + (Version.Length > 0 ? $", {Version}" : "")
                                   + ". They apply when the game starts, with GoldHEN's Game Patch plugin loaded.";
                if (PatchXml == null)
                {
                    SetPatchStatus($"The database has no patches for {Item.TitleId}.");
                    return;
                }

                Patches = GamePatches.Parse(Item.TitleId, PatchXml, Item.Title.Version)
                    .OrderByDescending(x => x.Applies).ThenBy(x => x.Name).ToList();
                SetPatchStatus("Reading what's switched on…");
                await GamePatches.ReadStateAsync(App.Config.PSIP.Trim(), Patches);
                foreach (var Patch in Patches)
                    Patch.PropertyChanged += (_, _) => SyncPatchSave();
                PatchList.ItemsSource = Patches;

                var Warning = await GamePatches.PluginWarningAsync(App.Config.PSIP.Trim());
                SetPatchStatus(Warning ?? (Patches.Any(x => x.Applies) ? "" :
                    $"None of these patches are for the installed version (v{Item.Title.Version})."));
            }
            catch (Exception ex)
            {
                SetPatchStatus("Couldn't load the patches: " + ex.Message);
            }
            finally
            {
                SyncPatchSave();
            }
        }

        void SetPatchStatus(string Text)
        {
            PatchStatus.Text = Text;
            PatchStatus.IsVisible = Text.Length > 0;
        }

        void SyncPatchSave()
        {
            bool Changed = Patches.Any(x => x.Enabled != x.WasEnabled);
            bool Ready = PatchXml != null && ConsoleStatus.Instance.FtpOpen && !PatchBusy;
            BtnPatchSave.IsEnabled = Changed && Ready;
            ToolTip.SetTip(BtnPatchSave, PatchBusy ? "Saving…"
                : !ConsoleStatus.Instance.FtpOpen ? "Needs GoldHEN's FTP server running on the console"
                : !Changed ? "Switch a patch on or off first"
                : "Writes the patch file and your choices to /data/GoldHEN/patches on the console");
        }

        async System.Threading.Tasks.Task SavePatchesAsync()
        {
            if (PatchXml == null || PatchBusy)
                return;
            PatchBusy = true;
            SyncPatchSave();
            try
            {
                SetPatchStatus("Saving to the console…");
                await GamePatches.SaveAsync(App.Config.PSIP.Trim(), PatchTitleId, PatchXml, Patches);
                var On = Patches.Where(x => x.Enabled).Select(x => x.Name).ToList();
                SetPatchStatus(On.Count == 0 ? "Saved: every patch is off."
                    : $"Saved. On: {string.Join(", ", On)}. Start (or restart) the game to use them.");
            }
            catch (Exception ex)
            {
                SetPatchStatus("Couldn't save: " + ex.Message);
            }
            finally
            {
                PatchBusy = false;
                SyncPatchSave();
            }
        }

        async void MenuBackupSavesClick(object? sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is { } Item)
                await Model.BackupSavesAsync(Item);
        }

        async void MenuMoveClick(object? sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is { } Item)
                await MoveDialog.AskAndStartAsync(Item.TitleId, Item.Name, Item.Title.Category, !Item.OnExtended);
        }

        void MenuUninstallUpdateClick(object? sender, RoutedEventArgs e) => Uninstall(ItemOf(sender), "gp");

        /// <summary>Uninstall through RPI after an explicit confirmation, then read the console again.</summary>
        async void Uninstall(ConsoleTitleItem? Item, string Category)
        {
            if (Item == null || string.IsNullOrWhiteSpace(App.Config.PSIP))
                return;

            var What = Category == "gp" ? $"the installed update of {Item.Name}" : Item.Name;
            var Reply = await MessageBox.ShowAsync(
                $"Uninstall {What} ({Item.TitleId}) from the console?\n\nIt has to be downloaded again to play. Saved data is kept.",
                "Uninstall from PS4", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (Reply != DialogResult.Yes)
                return;

            var Error = await ConsoleActions.UninstallAsync(App.Config.PSIP, new LibraryEntry
            {
                Category = Category,
                TitleId = Item.TitleId,
                Title = Item.Name
            });

            if (Error != null)
            {
                await MessageBox.ShowAsync("Couldn't uninstall:\n" + Error, "Uninstall from PS4", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            ConsoleStatus.Instance.NotifyContentsChanged();
        }
    }
}
