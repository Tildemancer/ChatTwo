// TildeTools: written for this fork, not part of upstream Chat 2.

namespace ChatTwo;

public static class Hosting
{
    private static DirectoryInfo? Overridden;
    private static Func<Configuration>? Load;
    private static Func<Configuration, bool>? Save;

    public static bool IsHosted => Overridden != null;

    // Call before constructing Plugin.
    // Hosted, GetPluginConfig returns TT's config, so these settings get a file of their own.
    public static void HostIn(DirectoryInfo directory, Func<Configuration> load, Func<Configuration, bool> save)
    {
        (Overridden, Load, Save) = (directory, load, save);
        directory.Create();
    }

    public static DirectoryInfo DataDirectory => Overridden ?? Plugin.Interface.ConfigDirectory;

    public static Configuration LoadConfig() => Load?.Invoke() ?? Plugin.Interface.GetPluginConfig() as Configuration ?? new Configuration();

    public static void SaveConfig(Configuration config)
    {
        if (Save != null)
            Save(config);
        else
            Plugin.Interface.SavePluginConfig(config);
    }
}
