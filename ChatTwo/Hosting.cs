using Newtonsoft.Json;
using System.Reflection;
using Newtonsoft.Json.Serialization;

namespace ChatTwo;

/// <summary>
/// Where Chat 2 keeps its settings and data.
///
/// Normally that is its own plugin folder. When Chat 2 is compiled into another
/// plugin, Dalamud's config directory belongs to that host instead, and using it
/// would both lose the existing message database and overwrite the host's settings
/// file. Pointing this at the original folder keeps the data where it has always
/// been, so the standalone plugin and a hosted copy read the same history.
/// </summary>
public static class Hosting
{
    private static DirectoryInfo? Overridden;

    /// <summary>True when Chat 2 is running inside another plugin.</summary>
    public static bool IsHosted => Overridden != null;

    /// <summary>
    /// Redirects settings and data to a specific folder. Call before constructing
    /// <see cref="Plugin"/>.
    /// </summary>
    public static void HostIn(DirectoryInfo directory)
    {
        Overridden = directory;
        directory.Create();
    }

    /// <summary>The folder holding the database, emote cache and downloaded font.</summary>
    public static DirectoryInfo DataDirectory => Overridden ?? Plugin.Interface.ConfigDirectory;

    private static string ConfigPath => Path.Join(DataDirectory.Parent?.FullName ?? DataDirectory.FullName, "ChatTwo.json");

    /// <summary>
    /// Matches how Dalamud writes plugin settings, so the same file is readable
    /// whether Chat 2 is running on its own or inside a host. The stored objects
    /// carry a "$type" and will not load without the matching type handling.
    /// </summary>
    private static readonly JsonSerializerSettings SerializerSettings = new()
    {
        TypeNameHandling = TypeNameHandling.Objects,
        TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
        SerializationBinder = new LocalAssemblyBinder(),
    };

    /// <summary>
    /// Resolves the types named in the settings file against the copy of Chat 2
    /// that is actually running.
    ///
    /// Left to itself the serializer looks a type up by assembly name, which loads
    /// a second copy of this assembly into a different context. The result is two
    /// types with identical names that the runtime considers unrelated, and an
    /// error saying a type is not compatible with itself.
    /// </summary>
    private sealed class LocalAssemblyBinder : DefaultSerializationBinder
    {
        private static readonly Assembly Ours = typeof(Configuration).Assembly;
        private static readonly string OurName = Ours.GetName().Name!;

        public override Type BindToType(string? assemblyName, string typeName)
        {
            // Resolved as a whole rather than by testing our assembly first,
            // because the name can be a generic owned by the runtime whose
            // arguments are ours: Dictionary<ChatType, ChatSource> belongs to
            // CoreLib, but both of its arguments must come from this copy. Type
            // resolution calls the hook for every assembly named anywhere in the
            // name, so nesting is handled wherever it appears.
            var qualified = assemblyName == null ? typeName : $"{typeName}, {assemblyName}";

            var resolved = Type.GetType(qualified, ResolveAssembly, ResolveType, throwOnError: false);
            if (resolved != null)
                return resolved;

            return base.BindToType(assemblyName, typeName);
        }

        private static Assembly? ResolveAssembly(AssemblyName name) =>
            string.Equals(name.Name, OurName, StringComparison.Ordinal)
                ? Ours
                : Assembly.Load(name);

        private static Type? ResolveType(Assembly? assembly, string name, bool ignoreCase) =>
            assembly == null
                ? Type.GetType(name, throwOnError: false, ignoreCase)
                : assembly.GetType(name, throwOnError: false, ignoreCase);
    }

    /// <summary>
    /// Reads the configuration. When hosted this cannot go through Dalamud, because
    /// Dalamud would hand back the host plugin's configuration object.
    /// </summary>
    /// <summary>
    /// Set when settings existed but could not be read. Saving is then refused, so
    /// a file that failed to load is never replaced by the defaults that stood in
    /// for it. Losing settings to a read bug is bad; overwriting them afterwards
    /// makes it unrecoverable.
    /// </summary>
    private static bool LoadFailed;

    public static Configuration LoadConfig()
    {
        if (!IsHosted)
            return Plugin.Interface.GetPluginConfig() as Configuration ?? new Configuration();

        LoadFailed = false;

        try
        {
            var path = ConfigPath;
            if (!File.Exists(path))
                return new Configuration();

            var loaded = JsonConvert.DeserializeObject<Configuration>(File.ReadAllText(path), SerializerSettings);
            if (loaded != null)
                return loaded;

            LoadFailed = true;
            Plugin.Log.Error($"Chat 2's configuration at {path} read as empty; it will not be overwritten.");
        }
        catch (Exception ex)
        {
            LoadFailed = true;
            Plugin.Log.Error(ex, $"Could not read Chat 2's configuration at {ConfigPath}. " +
                                 "Running on defaults; the file will NOT be overwritten.");
        }

        return new Configuration();
    }

    /// <summary>Writes the configuration back to wherever it was read from.</summary>
    public static void SaveConfig(Configuration config)
    {
        if (!IsHosted)
        {
            Plugin.Interface.SavePluginConfig(config);
            return;
        }

        if (LoadFailed)
        {
            Plugin.Log.Warning(
                "Refusing to save Chat 2's configuration: the existing one could not be read, " +
                "and writing now would replace it with defaults.");
            return;
        }

        try
        {
            var path = ConfigPath;
            var json = JsonConvert.SerializeObject(config, Formatting.Indented, SerializerSettings);

            // Written beside the target and moved into place, so an interrupted or
            // failed write cannot leave a half-file where the settings used to be.
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, json);

            if (File.Exists(path))
                File.Replace(temporary, path, path + ".bak", ignoreMetadataErrors: true);
            else
                File.Move(temporary, path);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Could not save Chat 2's configuration.");
        }
    }
}
