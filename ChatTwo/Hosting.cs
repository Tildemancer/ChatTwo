using Newtonsoft.Json;
using System.Reflection;
using Newtonsoft.Json.Serialization;

namespace ChatTwo;

/// <summary>
/// Where Chat 2 keeps its settings and data. When hosted inside another plugin,
/// points at Chat 2's own folder rather than the host's config directory.
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

    /// <summary>Matches how Dalamud writes plugin settings; stored objects carry a "$type".</summary>
    private static readonly JsonSerializerSettings SerializerSettings = new()
    {
        TypeNameHandling = TypeNameHandling.Objects,
        TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
        SerializationBinder = new LocalAssemblyBinder(),
    };

    /// <summary>
    /// Resolves types named in the settings file against the running copy of Chat 2;
    /// without this the serializer loads a second copy of the assembly.
    /// </summary>
    private sealed class LocalAssemblyBinder : DefaultSerializationBinder
    {
        private static readonly Assembly Ours = typeof(Configuration).Assembly;
        private static readonly string OurName = Ours.GetName().Name!;

        public override Type BindToType(string? assemblyName, string typeName)
        {
            // Resolved whole, not by testing our assembly first: a runtime generic
            // can have our types as its arguments.
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

    /// <summary>Settings existed but could not be read; saving is refused so defaults never overwrite them.</summary>
    private static bool LoadFailed;

    /// <summary>Reads the configuration. When hosted, Dalamud would return the host's config object.</summary>
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

            // Write beside the target and move into place, so a failed write leaves no half-file.
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
