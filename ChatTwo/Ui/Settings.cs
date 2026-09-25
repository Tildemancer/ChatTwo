using System.Numerics;
using ChatTwo.Resources;
using ChatTwo.Ui.SettingsTabs;
using ChatTwo.Util;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using Dalamud.Bindings.ImGui;

namespace ChatTwo.Ui;

public sealed class SettingsWindow : Window
{
    private readonly Plugin Plugin;

    private Configuration Mutable { get; }
    private List<ISettingsTab> Tabs { get; }
    private int CurrentTab;

    public SettingsWindow(Plugin plugin) : base($"{Language.Settings_Title.Format(Plugin.PluginName)}###chat2-settings")
    {
        Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(475, 600),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        Plugin = plugin;
        Mutable = new Configuration();

        Tabs =
        [
            new Display(Mutable),
            new ChatLogConfig(Plugin, Mutable),
            new Emote(Plugin, Mutable),
            new Preview(Mutable),
            new Fonts(Mutable),
            new ChatColours(Plugin, Mutable),
            new Tabs(Plugin, Mutable),
            new Database(Plugin, Mutable),
            new Webinterface(Plugin, Mutable),
            new Miscellaneous(Mutable),
            new About()
        ];

        // TildeTools
        // Hosted, the changelog's manifest is ours, so the tab would be empty and mislabelled
        if (!Hosting.IsHosted)
            Tabs.Insert(Tabs.Count - 1, new Changelog(Mutable));
        // TildeTools ends

        RespectCloseHotkey = false;
        DisableWindowSounds = true;

        Initialise();

        Plugin.Commands.Register("/chat2", "Perform various actions with Chat 2.").Execute += Command;
        Plugin.Interface.UiBuilder.OpenConfigUi += Toggle;
    }

    public void Dispose()
    {
        Plugin.Interface.UiBuilder.OpenConfigUi -= Toggle;
        Plugin.Commands.Register("/chat2").Execute -= Command;
    }

    private void Command(string command, string args)
    {
        if (string.IsNullOrWhiteSpace(args))
            Toggle();
    }

    private void Initialise()
    {
        Mutable.UpdateFrom(Plugin.Config, false);
    }

    public override void Draw()
    {
        if (ImGui.IsWindowAppearing())
            Initialise();

        using (var table = ImRaii.Table("##chat2-settings-table", 2))
        {
            if (table.Success)
            {
                ImGui.TableSetupColumn("tab", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("settings", ImGuiTableColumnFlags.WidthStretch);

                ImGui.TableNextColumn();

                var changed = false;
                for (var i = 0; i < Tabs.Count; i++)
                {
                    if (!ImGui.Selectable($"{Tabs[i].Name}###tab-{i}", CurrentTab == i))
                        continue;

                    CurrentTab = i;
                    changed = true;
                }

                ImGui.TableNextColumn();

                var style = ImGui.GetStyle();
                var height = ImGui.GetContentRegionAvail().Y - style.FramePadding.Y * 2 - style.ItemSpacing.Y - style.ItemInnerSpacing.Y * 2 - ImGui.CalcTextSize("A").Y;

                using var child = ImRaii.Child("##chat2-settings", new Vector2(-1, height));
                if (child.Success)
                    Tabs[CurrentTab].Draw(changed);
            }
        }

        ImGui.Separator();

        var save = ImGui.Button(Language.Settings_Save);

        ImGui.SameLine();

        if (ImGui.Button(Language.Settings_SaveAndClose)) {
            save = true;
            IsOpen = false;
        }

        ImGui.SameLine();

        if (ImGui.Button(Language.Settings_Discard)) {
            IsOpen = false;
        }

        // TildeTools
        // Anna's and Infi's Ko-fi links live on our Credits tab
        // If we're a third party plugin of a third party tool, are we a ninth party plugin?
        // TildeTools ends

        if (!save)
            return;

        // calculate all conditions before updating config
        var hideChanged = !Mutable.HideChat && Mutable.HideChat != Plugin.Config.HideChat;
        var languageChanged = Mutable.LanguageOverride != Plugin.Config.LanguageOverride;
        var fontChanged = Mutable.GlobalFontV2 != Plugin.Config.GlobalFontV2
                          || Mutable.JapaneseFontV2 != Plugin.Config.JapaneseFontV2
                          || Mutable.ItalicFontV2 != Plugin.Config.ItalicFontV2
                          || Mutable.ExtraGlyphRanges != Plugin.Config.ExtraGlyphRanges;
        var fontSizeChanged = Math.Abs(Mutable.SymbolsFontSizeV2 - Plugin.Config.SymbolsFontSizeV2) > 0.001
                          || Math.Abs(Mutable.FontSizeV2 - Plugin.Config.FontSizeV2) > 0.001;
        var italicStateChanged = Mutable.ItalicEnabled != Plugin.Config.ItalicEnabled;

        Plugin.Config.UpdateFrom(Mutable, true);

        // save after 60 frames have passed, which should hopefully not
        // commit any changes that cause a crash
        Plugin.DeferredSaveFrames = 60;
        Plugin.MessageManager.ClearAllTabs();
        Plugin.MessageManager.FilterAllTabsAsync();

        if (fontChanged || fontSizeChanged || italicStateChanged)
            Plugin.FontManager.BuildFonts();

        if (languageChanged)
            Plugin.LanguageChanged(Plugin.Interface.UiLanguage);

        if (hideChanged)
            GameFunctions.GameFunctions.SetChatInteractable(true);

        if (Plugin.Config.ShowEmotes)
            Task.Run(EmoteCache.LoadData);

        Initialise();
    }
}
