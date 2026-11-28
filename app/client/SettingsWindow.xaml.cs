// SPDX-License-Identifier: GPL-2.0-only
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NekoDrop;

/// <summary>
/// Settings in four tabs (the design's pivot): general (name, folder,
/// language, accent, start with Windows, hotkey), network and receiving
/// (mode, ask first, always-accept list), card and advanced (detected or
/// chosen card, experimental switches, diagnostic report), about and updates.
/// Save keeps the window open and says so; Cancel closes it.
/// </summary>
public partial class SettingsWindow : Window
{
    static App A => App.Instance;
    string _folder;
    readonly List<string> _trusted;
    string _releasesUrl = Updater.ReleasesPage;

    public SettingsWindow(string tab = "general")
    {
        InitializeComponent();
        Native.Chrome(this);
        var s = A.Settings;
        NameBox.Text = s.DeviceName;
        _folder = s.SaveFolder;
        FolderText.Text = _folder;
        (s.AutoAcceptMode switch
        {
            "everyone" => AutoAcceptEveryone,
            "contacts" => AutoAcceptContacts,
            _ => AutoAcceptOff,
        }).IsChecked = true;
        (s.MediaFormat == "original" ? MediaOriginal : MediaCompatible).IsChecked = true;
        AutoConvertHeicBox.IsChecked = s.AutoConvertHeic;
        StartupBox.IsChecked = s.StartWithWindows;
        HotkeyBox.IsChecked = s.Hotkey;
        ShowSpeedBox.IsChecked = s.ShowTransferSpeed;
        AppleIdOnBox.IsChecked = s.AppleIdOn;
        AppleIdOn_Changed(AppleIdOnBox, new RoutedEventArgs());
        MtaOnBox.IsChecked = s.MtaOn;
        LocalSendOnBox.IsChecked = s.LocalSendOn;
        QuickShareOnBox.IsChecked = s.QuickShareOn;
        MtaRelayBox.IsChecked = s.RelayOn;
        MtaRelayKeepLocalBox.IsChecked = s.MtaRelayKeepLocal;
        _relayRules = Relay.Load(s.RelayRules);
        FillRelayChoices();
        ShowRelayRules();
        MtaOn_Changed(MtaOnBox, new RoutedEventArgs());
        ShowReceive();
        ShowIdentity();
        _hotkeyKeys = Hotkey.TryParse(s.HotkeyKeys, out _, out _) ? s.HotkeyKeys : Hotkey.Default;
        ShowHotkey();
        LanguageBox.SelectedItem = LanguageBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, s.Language, StringComparison.Ordinal)) ?? LanguageBox.Items[0];
        BuildAccents(s.Accent);
        (s.HubIcon switch { "desktop" => HubDesktop, "laptop" => HubLaptop, _ => HubAuto }).IsChecked = true;
        _trusted = [.. s.AlwaysAccept];
        ShowTrusted();
        var mode = (string?)A.Service.Last?["mode"] ?? "concurrent";
        ModeConcurrent.IsChecked = mode == "concurrent";
        ModeOnDemand.IsChecked = mode == "ondemand";
        ModeOff.IsChecked = mode == "off";
        FastUnlockBox.IsChecked = (bool?)A.Service.Last?["fastUnlock"] ?? false;
        CoexPlanBox.IsChecked = (bool?)A.Service.Last?["coexPlan"] ?? false;
        HighThroughputBox.IsChecked = (bool?)A.Service.Last?["highThroughput"] ?? false;
        DataPlaneAccelBox.IsChecked = (bool?)A.Service.Last?["dataPlaneAccel"] ?? false;
        StationWifi6Box.IsChecked = (bool?)A.Service.Last?["stationWifi6"] ?? false;
        KeepMacBox.IsChecked = (bool?)A.Service.Last?["keepWindowsMac"] ?? true;
        QuickReleaseBox.IsChecked = (bool?)A.Service.Last?["quickReleaseOnDemand"] ?? false;
        var idleSec = (int?)A.Service.Last?["idleSeconds"] ?? 90;
        (idleSec <= 15 ? Idle15 : idleSec <= 30 ? Idle30 : idleSec <= 60 ? Idle60 : Idle90).IsChecked = true;
        CheckUpdatesBox.IsChecked = s.CheckUpdates;
        PreviewUpdatesBox.IsChecked = s.PreviewUpdates;
        DeleteInstallerBox.IsChecked = s.DeleteInstallerAfterInstall;
        (s.UpdateLaunch == "background" ? UpdateTray : UpdateWindow).IsChecked = true;
        VersionText.Text = "nekodrop " + Updater.Current;
        Title = L.T("set.title") + " · nekodrop " + Updater.Current;
        ShowCard(A.Service.Last);
        A.Service.Reply += OnCardsReply;
        Closed += (_, _) => A.Service.Reply -= OnCardsReply;
        A.Service.RequestCards();
        (tab switch { "network" => TabNetwork, "appleid" => TabAppleId, "mta" => TabMta, "hardware" => TabHardware, "updates" => TabUpdates, _ => TabGeneral }).IsChecked = true;
    }

    void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (PageGeneral is null) return; // still loading
        PageGeneral.Visibility = TabGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageNetwork.Visibility = TabNetwork.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageAppleId.Visibility = TabAppleId.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageMta.Visibility = TabMta.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageHardware.Visibility = TabHardware.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageUpdates.Visibility = TabUpdates.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------- accent

    /// <summary>
    /// One choice in one row: colourful first (each state in its own colour on
    /// Windows' accent), then "same as Windows" and the four accents of the design.
    /// </summary>
    void BuildAccents(string chosen)
    {
        var rainbow = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        foreach (var (hex, at) in new[] { ("#0067C0", 0.0), ("#0F7B0F", 0.35), ("#FCE100", 0.65), ("#C42B1C", 1.0) })
            rainbow.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(hex)!, at));
        AccentPanel.Children.Add(new RadioButton
        {
            Style = (Style)FindResource("Swatch"),
            Tag = Theme.Colourful,
            GroupName = "accent",
            Background = rainbow,
            ToolTip = L.T("set.colourful") + " · " + L.T("set.colourfulHint"),
            IsChecked = chosen == Theme.Colourful,
            Margin = new Thickness(0, 0, 10, 0),
        });
        var system = new RadioButton
        {
            Style = (Style)FindResource("Segment"),
            Content = L.T("set.accent.system"),
            Tag = null,
            GroupName = "accent",
            IsChecked = chosen != Theme.Colourful && Theme.Accents.All(a => a.Key != chosen),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        system.SetValue(FrameworkElement.NameProperty, "AccentSystem");
        AccentPanel.Children.Add(system);
        foreach (var a in Theme.Accents)
        {
            AccentPanel.Children.Add(new RadioButton
            {
                Style = (Style)FindResource("Swatch"),
                Tag = a.Key,
                GroupName = "accent",
                Background = new SolidColorBrush(Theme.Dark ? a.Dark : a.Light),
                ToolTip = a.Key,
                IsChecked = a.Key == chosen,
            });
        }
    }

    string ChosenAccent() =>
        AccentPanel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true && r.Tag is string)?.Tag as string ?? "";

    // ----------------------------------------------------- always accept

    /// <summary>
    /// The list could only shrink: nothing added to it (#45). Offers the
    /// senders that asked lately, by the same name and model the check uses.
    /// </summary>
    void TrustedAdd_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = TrustedAdd, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var key in A.Settings.RecentSenders.Where(k => !_trusted.Contains(k)))
        {
            var item = new MenuItem { Header = key.Replace("|", " · ") };
            item.Click += (_, _) => { _trusted.Add(key); ShowTrusted(); };
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = L.T("set.trustedAddNone"), IsEnabled = false });
        menu.IsOpen = true;
    }

    void ShowTrusted()
    {
        TrustedTitle.Text = L.F("set.trustedCount", _trusted.Count);
        TrustedEmpty.Visibility = _trusted.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TrustedList.Children.Clear();
        foreach (var t in _trusted)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            var remove = new Button { Style = (Style)FindResource("WinButton"), Content = L.T("set.remove"), Padding = new Thickness(10, 4, 10, 4) };
            remove.Click += (_, _) => { _trusted.Remove(t); ShowTrusted(); };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(new TextBlock { Text = t.Replace("|", " · "), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            TrustedList.Children.Add(row);
        }
    }

    // ------------------------------------------------------------ the card

    /// <summary>A row of the card or model list.</summary>
    sealed record Item(string Key, string Label, string Vendor = "");

    void ShowCard(JsonObject? state)
    {
        var card = (string?)state?["card"] ?? "";
        var manual = (string?)state?["manualCard"] ?? "";
        var found = card != "";
        CardStatus.Text = found ? (manual != "" ? L.F("set.card.manual", card) : card) : L.T("set.card.none");
        CardBadgeText.Text = found ? L.T("set.card.detectedBadge") : L.T("set.card.unrecognized");
        CardBadge.SetResourceReference(Border.BackgroundProperty, found ? "SuccessBg" : "WarnBg");
        CardBadgeText.SetResourceReference(TextBlock.ForegroundProperty, found ? "Success" : "Warn");
        // The manual choice is for a card nekodrop does not know; shown when none was found or one was chosen by hand.
        CardManual.Visibility = !found || manual != "" ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The service's card list and models (the "cards" event); a manual choice's answer ("card-manual").</summary>
    void OnCardsReply(JsonObject o) => Dispatcher.BeginInvoke(() =>
    {
        var ev = (string?)o["event"];
        if (ev == "card-manual")
        {
            var error = (string?)o["error"] ?? "";
            CardError.Text = error == "" ? L.T("set.card.applied") : L.T("set.card.mismatch");
            return;
        }
        if (ev != "cards") return;
        var manualHw = (string?)A.Service.Last?["manualCard"] ?? "";
        var manualModel = (string?)A.Service.Last?["manualModel"] ?? "";
        CardBox.Items.Clear();
        foreach (var c in o["candidates"]?.AsArray() ?? [])
        {
            var hw = (string?)c?["hw"] ?? "";
            var name = (string?)c?["name"] ?? "";
            var tag = (bool?)c?["recognized"] == true ? L.T("set.card.recognized") : L.T("set.card.unrecognized");
            CardBox.Items.Add(new Item(hw, $"{(name == "" ? hw : name)} ({hw.Replace("PCI\\", "")}) · {tag}"));
        }
        ModelBox.Items.Clear();
        foreach (var m in o["models"]?.AsArray() ?? [])
            ModelBox.Items.Add(new Item((string?)m?["key"] ?? "", (string?)m?["name"] ?? "", (string?)m?["vendor"] ?? ""));
        CardBox.SelectedItem = CardBox.Items.Cast<Item>().FirstOrDefault(i => i.Key == manualHw) ??
                               CardBox.Items.Cast<Item>().FirstOrDefault();
        ModelBox.SelectedItem = ModelBox.Items.Cast<Item>().FirstOrDefault(i => i.Key == manualModel);
        if (CardBox.Items.Count == 0) CardError.Text = L.T("set.card.nolist");
    });

    void CardApply_Click(object sender, RoutedEventArgs e)
    {
        if (CardBox.SelectedItem is not Item card || ModelBox.SelectedItem is not Item model)
        {
            CardError.Text = L.T("set.card.pick");
            return;
        }
        CardError.Text = "";
        A.Service.SetManualCard(card.Key, model.Key);
    }

    void CardAuto_Click(object sender, RoutedEventArgs e)
    {
        CardError.Text = "";
        A.Service.SetManualCard("", "");
    }

    void Report_Click(object sender, RoutedEventArgs e) => App.RunReport();

    // ------------------------------------------------------------- updates

    void Status(string text, bool releases = false, double? progress = null)
    {
        UpdateBox.Visibility = Visibility.Visible;
        UpdateStatus.Text = text;
        OpenReleasesButton.Visibility = releases ? Visibility.Visible : Visibility.Collapsed;
        UpdateBox.SetResourceReference(Border.BackgroundProperty, releases ? "WarnBg" : "AccentSubtle");
        UpdateStatus.SetResourceReference(TextBlock.ForegroundProperty, releases ? "Warn" : "Text");
        UpdateBarBox.Visibility = progress is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateBar.Progress = progress ?? 0;
    }

    static string FormatSpeed(long bytesPerSecond)
    {
        if (bytesPerSecond >= 1024 * 1024)
            return $"{bytesPerSecond / (1024.0 * 1024.0):0.0} MB/s";
        return $"{bytesPerSecond / 1024.0:0.0} KB/s";
    }

    async void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        CheckNowButton.IsEnabled = false;
        Status(L.T("update.checking"));
        try
        {
            A.Settings.PreviewUpdates = PreviewUpdatesBox.IsChecked == true;
            A.Settings.DeleteInstallerAfterInstall = DeleteInstallerBox.IsChecked == true;
            A.Settings.UpdateLaunch = UpdateTray.IsChecked == true ? "background" : "window";
            var res = await A.CheckForUpdateDetailed(true);
            switch (res.Status)
            {
                case Updater.CheckStatus.UpToDate:
                    Status(L.T("update.none"));
                    return;
                case Updater.CheckStatus.RateLimited:
                    _releasesUrl = res.PageUrl ?? Updater.ReleasesPage;
                    Status(res.NewerTag is not null ? L.F("update.rateLimitedFound", res.NewerTag) : L.T("update.rateLimited"), releases: true);
                    return;
                case Updater.CheckStatus.Failed:
                    _releasesUrl = res.PageUrl ?? Updater.ReleasesPage;
                    Status(res.NewerTag is not null
                        ? L.F("update.checkFailedFound", res.NewerTag)
                        : L.T("update.checkFailed") + (string.IsNullOrEmpty(res.Detail) ? "" : ": " + res.Detail),
                        releases: true);
                    return;
            }
            var found = res.Release;
            if (found is null) { Status(L.T("update.none")); return; }
            var text = L.F("update.found", found.Version.ToString());
            Status(text, progress: 0);
            var progress = new Progress<Updater.DownloadProgress>(p =>
                Status($"{text} {p.Percent}% · {FormatSpeed(p.BytesPerSecond)}", progress: p.Percent / 100.0));
            var why = await A.InstallUpdate(progress);
            if (why != "")
            {
                _releasesUrl = string.IsNullOrWhiteSpace(found.Page) ? Updater.ReleasesPage : found.Page;
                Status(L.T("update.failed") + ": " + why, releases: true);
            }
        }
        finally { CheckNowButton.IsEnabled = true; }
    }

    void OpenReleases_Click(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_releasesUrl) { UseShellExecute = true }); }
        catch (Exception) { }
    }

    // ---------------------------------------------------------------- save

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = L.T("pick.folder"), InitialDirectory = _folder };
        if (Native.ShowDialog(this, dlg, dlg.Title) != true) return;
        _folder = dlg.FolderName;
        FolderText.Text = _folder;
    }

    async void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = A.Settings;
        var name = NameBox.Text.Trim();
        if (name.Length > 0) s.DeviceName = name;
        s.SaveFolder = _folder;
        s.AutoAcceptMode = AutoAcceptEveryone.IsChecked == true ? "everyone" : AutoAcceptContacts.IsChecked == true ? "contacts" : "off";
        s.Ask = s.AutoAcceptMode != "everyone";
        var mediaFormat = MediaOriginal.IsChecked == true ? "original" : "compatible";
        var mediaChanged = mediaFormat != s.MediaFormat;
        s.MediaFormat = mediaFormat;
        s.AutoConvertHeic = AutoConvertHeicBox.IsChecked == true;
        s.StartWithWindows = StartupBox.IsChecked == true;
        var appleIdOn = AppleIdOnBox.IsChecked == true;
        var receive = appleIdOn && ReceiveContacts.IsChecked == true && AppleId.Present ? "contacts" : "everyone";
        var receiveChanged = receive != s.Receive || appleIdOn != s.AppleIdOn;
        s.AppleIdOn = appleIdOn;
        s.Receive = receive;
        s.MtaOn = MtaOnBox.IsChecked == true;
        s.LocalSendOn = LocalSendOnBox.IsChecked == true;
        s.QuickShareOn = QuickShareOnBox.IsChecked == true;
        s.RelayOn = MtaRelayBox.IsChecked == true;
        s.RelayRules = Relay.Save(_relayRules);
        s.MtaRelayKeepLocal = MtaRelayKeepLocalBox.IsChecked == true;
        var hotkeyChanged = (HotkeyBox.IsChecked == true) != s.Hotkey || _hotkeyKeys != s.HotkeyKeys;
        s.Hotkey = HotkeyBox.IsChecked == true;
        s.HotkeyKeys = _hotkeyKeys;
        s.ShowTransferSpeed = ShowSpeedBox.IsChecked == true;
        s.CheckUpdates = CheckUpdatesBox.IsChecked == true;
        s.PreviewUpdates = PreviewUpdatesBox.IsChecked == true;
        s.DeleteInstallerAfterInstall = DeleteInstallerBox.IsChecked == true;
        s.UpdateLaunch = UpdateTray.IsChecked == true ? "background" : "window";
        var language = (LanguageBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        var languageChanged = language != s.Language;
        s.Language = language;
        s.AlwaysAccept = [.. _trusted];
        s.HubIcon = HubDesktop.IsChecked == true ? "desktop" : HubLaptop.IsChecked == true ? "laptop" : "auto";
        var accent = ChosenAccent();
        var accentChanged = accent != s.Accent;
        s.Accent = accent;
        s.Save();
        try { Shell.Register(s.StartWithWindows); } catch (Exception) { }
        A.Engine.Apply(s);
        A.ApplyMta();
        var mode = ModeConcurrent.IsChecked == true ? "concurrent" : ModeOnDemand.IsChecked == true ? "ondemand" : "off";
        if (mode != (string?)A.Service.Last?["mode"]) A.Service.SetMode(mode);
        var fastUnlock = FastUnlockBox.IsChecked == true;
        if (fastUnlock != ((bool?)A.Service.Last?["fastUnlock"] ?? false)) A.Service.SetFastUnlock(fastUnlock);
        var coexPlan = CoexPlanBox.IsChecked == true;
        if (coexPlan != ((bool?)A.Service.Last?["coexPlan"] ?? false)) A.Service.SetCoexPlan(coexPlan);
        var highThroughput = HighThroughputBox.IsChecked == true;
        if (highThroughput != ((bool?)A.Service.Last?["highThroughput"] ?? false)) A.Service.SetHighThroughput(highThroughput);
        var dataPlaneAccel = DataPlaneAccelBox.IsChecked == true;
        if (dataPlaneAccel != ((bool?)A.Service.Last?["dataPlaneAccel"] ?? false)) A.Service.SetDataPlaneAccel(dataPlaneAccel);
        var keepMac = KeepMacBox.IsChecked == true;
        if (keepMac != ((bool?)A.Service.Last?["keepWindowsMac"] ?? true)) A.Service.SetKeepWindowsMac(keepMac);
        var quickRelease = QuickReleaseBox.IsChecked == true;
        if (quickRelease != ((bool?)A.Service.Last?["quickReleaseOnDemand"] ?? false)) A.Service.SetQuickReleaseOnDemand(quickRelease);
        var idleSec = Idle15.IsChecked == true ? 15 : Idle30.IsChecked == true ? 30 : Idle60.IsChecked == true ? 60 : 90;
        if (idleSec != ((int?)A.Service.Last?["idleSeconds"] ?? 90)) A.Service.SetIdleSeconds(idleSec);
        var stationWifi6 = StationWifi6Box.IsChecked == true;
        if (stationWifi6 != ((bool?)A.Service.Last?["stationWifi6"] ?? false)) A.Service.SetStationWifi6(stationWifi6);
        A.Window?.FolderChanged();
        var hotkeyTaken = hotkeyChanged && !A.ApplyHotkey();
        if (receiveChanged || mediaChanged) A.RestartEngine();
        A.Window?.ShowMark();
        if (accentChanged) A.ApplyTheme();
        if (languageChanged) { A.ApplyLanguage(); Close(); return; }
        SavedText.Text = hotkeyTaken ? L.F("set.hotkeyTaken", Hotkey.Show(_hotkeyKeys)) : "✓ " + L.T("set.saved");
        await Task.Delay(2000);
        SavedText.Text = "";
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    // ------------------------------------------------ Apple ID, Contacts Only

    /// <summary>Who can send, as saved (also when the AirDrop mark changed it while this window is open).</summary>
    public void ShowReceive()
    {
        (A.Settings.Receive == "contacts" && AppleId.Present ? ReceiveContacts : ReceiveEveryone).IsChecked = true;
    }

    /// <summary>The page below the experimental switch works only while it is on.</summary>
    void AppleIdOn_Changed(object sender, RoutedEventArgs e)
    {
        if (AppleIdBody is null) return; // still loading
        AppleIdBody.IsEnabled = AppleIdOnBox.IsChecked == true;
        AppleIdBody.Opacity = AppleIdBody.IsEnabled ? 1 : 0.5;
    }

    /// <summary>What identity there is, and which buttons make sense with it.</summary>
    void ShowIdentity()
    {
        var present = AppleId.Present;
        ReceiveContacts.IsEnabled = present;
        if (!present) ReceiveEveryone.IsChecked = true;
        SignInButton.IsEnabled = AppleId.CanSignIn;
        SignInButton.ToolTip = AppleId.CanSignIn ? null : L.T("aid.noTool");
        ExportAppleIdLogButton.Visibility = AppleId.HasLog ? Visibility.Visible : Visibility.Collapsed;
        SignOutButton.Visibility = present || AppleId.HasLocalState ? Visibility.Visible : Visibility.Collapsed;
        ContactsButton.Visibility = AppleId.Source == "appleid" ? Visibility.Collapsed : Visibility.Visible;
        if (!present) { IdentityText.Text = L.T("aid.none"); return; }
        var until = AppleId.CertificateUntil is { } d ? d.ToString("yyyy-MM-dd") : "?";
        var who = AppleId.Source == "appleid" ? L.F("aid.signedIn", AppleId.Account) : L.T("aid.fromMac");
        IdentityText.Text = who + " · " + L.F("aid.until", until) + " · " + L.F("aid.contactCount", AppleId.ContactCount);
    }

    void IdentityChanged()
    {
        ShowIdentity();
        A.RestartEngine();
    }

    void SignIn_Click(object sender, RoutedEventArgs e)
    {
        var w = new AppleIdWindow { Owner = this };
        if (w.ShowDialog() == true)
        {
            IdentityChanged();
            SavedText.Text = "✓ " + L.T("aid.done");
        }
        else
        {
            ShowIdentity();
        }
    }

    void ExportAppleIdLog_Click(object sender, RoutedEventArgs e)
    {
        if (!AppleId.HasLog) { MessageBox.Show(this, L.T("aid.noLog"), "nekodrop", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmm");
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = L.T("aid.exportLog"),
            Filter = "Log (*.txt)|*.txt",
            FileName = $"nekodrop-appleid-log-{stamp}.txt",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (Native.ShowDialog(this, dlg, dlg.Title) != true) return;
        try
        {
            AppleId.ExportLog(dlg.FileName);
            SavedText.Text = "✓ " + L.F("aid.logExported", System.IO.Path.GetFileName(dlg.FileName));
            try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{dlg.FileName}\""); } catch (Exception) { }
        }
        catch (Exception x) { MessageBox.Show(this, x.Message, "nekodrop", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    void ImportMac_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = L.T("aid.pickMac") };
        if (Native.ShowDialog(this, dlg, dlg.Title) != true) return;
        var why = AppleId.ImportFromMac(dlg.FolderName, null);
        if (why == "need_passphrase")
        {
            var pass = Microsoft.VisualBasic.Interaction.InputBox(L.T("aid.passphrase"), "nekodrop");
            if (pass.Length == 0) return;
            why = AppleId.ImportFromMac(dlg.FolderName, pass);
        }
        if (why is not null) { MessageBox.Show(this, why, "nekodrop", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        IdentityChanged();
        SavedText.Text = "✓ " + L.T("aid.imported");
    }

    void ImportContacts_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = L.T("aid.pickVcf"), Filter = "vCard (*.vcf)|*.vcf" };
        if (Native.ShowDialog(this, dlg, dlg.Title) != true) return;
        try
        {
            var people = AppleId.ImportContacts(dlg.FileName);
            IdentityChanged();
            SavedText.Text = "✓ " + L.F("aid.contactsImported", people);
        }
        catch (Exception x) { MessageBox.Show(this, x.Message, "nekodrop", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    void SignOut_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, L.T("aid.signOutAsk"), "nekodrop", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try { AppleId.SignOut(); } catch (Exception x) { MessageBox.Show(this, x.Message, "nekodrop"); return; }
        if (A.Settings.Receive == "contacts") { A.Settings.Receive = "everyone"; A.Settings.Save(); }
        IdentityChanged();
    }

    // ---------------------------------------- Android Share (MTA) & Relay

    void MtaOn_Changed(object sender, RoutedEventArgs e)
    {
        if (MtaBody is null) return;
        MtaBody.IsEnabled = MtaOnBox.IsChecked == true;
        MtaBody.Opacity = MtaBody.IsEnabled ? 1 : 0.5;
        MtaRelay_Changed(MtaRelayBox, e);
    }

    void MtaRelay_Changed(object sender, RoutedEventArgs e)
    {
        if (RelayBody is null) return;
        RelayBody.IsEnabled = MtaRelayBox.IsChecked == true;
        RelayBody.Opacity = RelayBody.IsEnabled ? 1 : 0.5;
        ShowRelayRules();
    }

    /* A protocol switch changed: rules may start or stop working. */
    void RelayInputs_Changed(object sender, RoutedEventArgs e) => ShowRelayRules();

    List<RelayRule> _relayRules = [];

    /// <summary>A device in the "from" / "to" pickers.</summary>
    sealed class RelayChoice(RelayEnd end)
    {
        public RelayEnd End { get; } = end;
        public override string ToString() => End.Label;
    }

    /// <summary>The settings a rule is checked against: the switches as they are on this page now.</summary>
    Settings PreviewSettings() => new()
    {
        MtaOn = MtaOnBox.IsChecked == true,
        LocalSendOn = LocalSendOnBox.IsChecked == true,
        Discoverable = A.Settings.Discoverable,
    };

    /// <summary>The pickers list the devices in the rules and every device now on the radar.</summary>
    void FillRelayChoices()
    {
        var ends = new List<RelayEnd>();
        void Add(RelayEnd e)
        {
            if (!ends.Any(x => x.Same(e))) ends.Add(e);
        }
        foreach (var r in _relayRules) { Add(r.From); Add(r.To); }
        foreach (var d in A.Model.Devices.Where(d => !d.ForAsk).ToList()) Add(RelayEnd.FromDevice(d));
        foreach (var box in new[] { RelayFromBox, RelayToBox })
        {
            var keep = (box.SelectedItem as RelayChoice)?.End;
            var items = ends.Select(e => new RelayChoice(e)).ToList();
            box.ItemsSource = items;
            box.SelectedItem = keep is null ? null : items.FirstOrDefault(i => i.End.Same(keep));
        }
    }

    void ShowRelayRules()
    {
        if (RelayRuleList is null) return;
        var settings = PreviewSettings();
        RelayRuleList.Children.Clear();
        foreach (var rule in _relayRules.ToList())
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var remove = new Button { Content = "✕", Style = (Style)FindResource("QuietButton"), ToolTip = L.T("relay.remove"), Padding = new Thickness(8, 2, 8, 2) };
            remove.Click += (_, _) => { _relayRules.Remove(rule); RelayStatus.Text = ""; ShowRelayRules(); };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = $"{rule.From.Label}  →  {rule.To.Label}", FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
            var why = Relay.Blocker(rule, settings);
            if (why.Length > 0)
                text.Children.Add(new TextBlock { Text = "⚠ " + why, Style = (Style)FindResource("Sub12"), Foreground = (Brush)FindResource("Warn") });
            row.Children.Add(text);
            RelayRuleList.Children.Add(row);
        }
        if (_relayRules.Count == 0)
            RelayRuleList.Children.Add(new TextBlock { Text = L.T("relay.noRules"), Style = (Style)FindResource("Sub12") });
        RenderRelayFlow();
    }

    void RelayFlow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) RenderRelayFlow();
    }

    /// <summary>
    /// The rules as a picture: each source on the left, this PC in the middle,
    /// each destination on the right, with an arrow for every rule; a rule
    /// that cannot work now is drawn dashed and grey.
    /// </summary>
    void RenderRelayFlow()
    {
        if (RelayFlow is null) return;
        var canvas = RelayFlow;
        canvas.Children.Clear();
        var width = canvas.ActualWidth;
        if (width < 200) return;
        var accent = (Brush)FindResource("Accent");
        var dim = (Brush)FindResource("TextSecondary");
        var border = (Brush)FindResource("Border");
        var card = (Brush)FindResource("Card");
        var settings = PreviewSettings();
        /* One colour per destination: its box and every arrow into it, so a rule can be followed through the PC. */
        /* Theme accent first, then fixed hues that read on light and dark (the theme's other colours are grey unless "colourful"). */
        Brush[] palette = [accent, Hue(0x2E, 0x9E, 0x5B), Hue(0xD0, 0x80, 0x10), Hue(0x8E, 0x5B, 0xD6)];
        static Brush Hue(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }

        var sources = new List<RelayEnd>();
        var targets = new List<RelayEnd>();
        foreach (var r in _relayRules)
        {
            if (!sources.Any(x => x.Same(r.From))) sources.Add(r.From);
            if (!targets.Any(x => x.Same(r.To))) targets.Add(r.To);
        }
        const double boxH = 36, gap = 10, top = 18, pcW = 92;
        var rows = Math.Max(1, Math.Max(sources.Count, targets.Count));
        var height = top + rows * boxH + (rows - 1) * gap + 22;
        canvas.Height = height;
        var colW = Math.Min(170, (width - pcW - 2 * 36) / 2);
        var pcX = (width - pcW) / 2;
        var midY = top + (rows * boxH + (rows - 1) * gap) / 2;

        void Label(string text, double x, double y, double w, Brush brush, double size = 11, TextAlignment align = TextAlignment.Center)
        {
            var t = new TextBlock { Text = text, Width = w, TextAlignment = align, FontSize = size, Foreground = brush, TextTrimming = TextTrimming.CharacterEllipsis };
            Canvas.SetLeft(t, x);
            Canvas.SetTop(t, y);
            canvas.Children.Add(t);
        }
        if (_relayRules.Count == 0)
        {
            /* Nothing to draw yet: just the two lines, not an empty PC box under the text. */
            canvas.Height = 48;
            Label(L.T("relay.noRulesFlow"), 0, 6, width, dim, 13);
            Label(L.T("relay.others"), 0, 28, width, dim);
            return;
        }
        Label(L.T("relay.colFrom"), 0, 0, colW, dim);
        Label(L.T("relay.colPc"), pcX, 0, pcW, dim);
        Label(L.T("relay.colTo"), width - colW, 0, colW, dim);

        double RowY(int i, int count)
        {
            var block = count * boxH + (count - 1) * gap;
            return midY - block / 2 + i * (boxH + gap);
        }
        void Box(RelayEnd e, double x, double y, Brush? edge = null)
        {
            var b = new Border
            {
                Width = colW, Height = boxH, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(edge is null ? 1 : 1.5),
                BorderBrush = edge ?? border, Background = card, Padding = new Thickness(8, 2, 8, 2),
            };
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new TextBlock { Text = e.Name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
            sp.Children.Add(new TextBlock { Text = RelayEnd.ProtocolLabel(e.Protocol), FontSize = 10, Foreground = dim });
            b.Child = sp;
            Canvas.SetLeft(b, x);
            Canvas.SetTop(b, y);
            canvas.Children.Add(b);
        }
        void Arrow(Point a, Point b, bool working, Brush color)
        {
            var brush = working ? color : dim;
            var fig = new PathFigure { StartPoint = a };
            var dx = (b.X - a.X) / 2;
            fig.Segments.Add(new BezierSegment(new Point(a.X + dx, a.Y), new Point(b.X - dx, b.Y), new Point(b.X - 6, b.Y), true));
            var path = new System.Windows.Shapes.Path { Data = new PathGeometry([fig]), Stroke = brush, StrokeThickness = working ? 2 : 1.5 };
            if (!working) path.StrokeDashArray = [3, 3];
            canvas.Children.Add(path);
            var head = new System.Windows.Shapes.Polygon
            {
                Points = [new Point(b.X, b.Y), new Point(b.X - 7, b.Y - 4), new Point(b.X - 7, b.Y + 4)],
                Fill = brush,
            };
            canvas.Children.Add(head);
        }

        for (int i = 0; i < sources.Count; i++) Box(sources[i], 0, RowY(i, sources.Count));
        for (int i = 0; i < targets.Count; i++) Box(targets[i], width - colW, RowY(i, targets.Count), palette[i % palette.Length]);
        var pc = new Border
        {
            Width = pcW, Height = 44, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            BorderBrush = (Brush)FindResource("BorderStrong"), Background = card,
            Child = new TextBlock { Text = "nekodrop", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        Canvas.SetLeft(pc, pcX);
        Canvas.SetTop(pc, midY - 22);
        canvas.Children.Add(pc);

        foreach (var r in _relayRules)
        {
            var working = Relay.Blocker(r, settings) == "";
            var si = sources.FindIndex(x => x.Same(r.From));
            var ti = targets.FindIndex(x => x.Same(r.To));
            var sy = RowY(si, sources.Count) + boxH / 2;
            var ty = RowY(ti, targets.Count) + boxH / 2;
            var color = palette[ti % palette.Length];
            Arrow(new Point(colW, sy), new Point(pcX, midY + (sy - midY) * 0.25), working, color);
            Arrow(new Point(pcX + pcW, midY + (ty - midY) * 0.25), new Point(width - colW, ty), working, color);
        }
        Label(L.T("relay.others"), 0, height - 16, width, dim);
    }

    RelayRule? PickedRule()
    {
        var from = (RelayFromBox.SelectedItem as RelayChoice)?.End;
        var to = (RelayToBox.SelectedItem as RelayChoice)?.End;
        if (from is null || to is null) { RelayStatus.Text = L.T("relay.pickBoth"); return null; }
        return new RelayRule(from, to);
    }

    void AddRelayRule(RelayRule rule)
    {
        var replaced = Relay.Add(_relayRules, rule);
        RelayStatus.Text = replaced switch
        {
            null => L.T("relay.samePair"),
            true => L.F("relay.replaced", rule.From.Name, rule.To.Name),
            false => "",
        };
    }

    void RelayAdd_Click(object sender, RoutedEventArgs e)
    {
        if (PickedRule() is not { } rule) return;
        AddRelayRule(rule);
        ShowRelayRules();
    }

    void RelayAddBoth_Click(object sender, RoutedEventArgs e)
    {
        if (PickedRule() is not { } rule) return;
        if (rule.From.Same(rule.To)) { RelayStatus.Text = L.T("relay.samePair"); return; }
        var replacedA = Relay.Add(_relayRules, rule);
        var replacedB = Relay.Add(_relayRules, new RelayRule(rule.To, rule.From));
        RelayStatus.Text = replacedA == true || replacedB == true ? L.T("relay.replacedSome") : "";
        ShowRelayRules();
    }

    async void RelayScan_Click(object sender, RoutedEventArgs e)
    {
        RelayScanButton.IsEnabled = false;
        RelayStatus.Text = L.T("relay.scanning");
        A.LookOn(RelayEnd.AirDrop);
        if (MtaOnBox.IsChecked == true) A.LookOn(RelayEnd.Mta);
        if (LocalSendOnBox.IsChecked == true) A.LookOn(RelayEnd.LocalSend);
        await Task.Delay(8000);
        A.StopLookingLater();
        FillRelayChoices();
        RelayScanButton.IsEnabled = true;
        RelayStatus.Text = "";
    }

    async void MtaDiag_Click(object sender, RoutedEventArgs e)
    {
        MtaDiagButton.IsEnabled = false;
        MtaDiagStatus.Text = L.T("mta.diagScanning");
        try
        {
            var report = await MtaEngine.ScanDiagnosticsAsync(8);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmm");
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = L.T("mta.diagButton"),
                Filter = "Diagnostic Report (*.txt)|*.txt",
                FileName = $"nekodrop-mta-ble-diag-{stamp}.txt",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            };
            if (Native.ShowDialog(this, dlg, dlg.Title) != true)
            {
                MtaDiagStatus.Text = "";
                return;
            }
            System.IO.File.WriteAllText(dlg.FileName, report, System.Text.Encoding.UTF8);
            MtaDiagStatus.Text = "✓ " + L.F("mta.diagSaved", System.IO.Path.GetFileName(dlg.FileName));
            SavedText.Text = MtaDiagStatus.Text;
            try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{dlg.FileName}\""); } catch (Exception) { }
        }
        catch (Exception x)
        {
            MtaDiagStatus.Text = x.Message;
            MessageBox.Show(this, x.Message, "nekodrop", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            MtaDiagButton.IsEnabled = true;
        }
    }

    // ------------------------------------------------- recording the shortcut

    string _hotkeyKeys = Hotkey.Default;
    bool _recording;

    void ShowHotkey() => HotkeyText.Text = _recording ? L.T("set.hotkeyPress") : Hotkey.Show(_hotkeyKeys);

    void HotkeyRecorder_Click(object sender, RoutedEventArgs e)
    {
        _recording = !_recording;
        ShowHotkey();
    }

    void HotkeyRecorder_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_recording) return;
        e.Handled = true;
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        if (key == System.Windows.Input.Key.Escape) { _recording = false; ShowHotkey(); return; }
        // Keyboard.Modifiers leaves out Win; ask for it directly.
        var mods = System.Windows.Input.Keyboard.Modifiers;
        if (System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.LWin) || System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.RWin))
            mods |= System.Windows.Input.ModifierKeys.Windows;
        if (Hotkey.FromKeys(mods, key) is not { } combo) return; // only modifiers so far, or no modifier
        _hotkeyKeys = combo;
        _recording = false;
        ShowHotkey();
    }

    void HotkeyRecorder_LostFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (!_recording) return;
        _recording = false;
        ShowHotkey();
    }

    void HotkeyReset_Click(object sender, RoutedEventArgs e)
    {
        _recording = false;
        _hotkeyKeys = Hotkey.Default;
        ShowHotkey();
    }
}
