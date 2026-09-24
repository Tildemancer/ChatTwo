// TildeTools: written for this fork, not part of upstream Chat 2.

using Newtonsoft.Json;
using System.Reflection;
using Newtonsoft.Json.Serialization;

namespace ChatTwo;

// Hosted, this points at Chat 2's own folder, not the host's config directory
public static class Hosting
{
    private static DirectoryInfo? Overridden;

    public static bool IsHosted => Overridden != null;

    // Before constructing Plugin
    public static void HostIn(DirectoryInfo directory)
    {
        Overridden = directory;
        directory.Create();
    }

    public static DirectoryInfo DataDirectory => Overridden ?? Plugin.Interface.ConfigDirectory;

    private static string ConfigPath => Path.Join(DataDirectory.Parent?.FullName ?? DataDirectory.FullName, "ChatTwo.json");

    // Matches how Dalamud writes settings, so stored objects carry "$type"
    private static readonly JsonSerializerSettings SerializerSettings = new()
    {
        TypeNameHandling = TypeNameHandling.Objects,
        TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
        SerializationBinder = new LocalAssemblyBinder(),
    };

    // Resolves against the running Chat 2, or the serializer loads a second copy of the assembly. Yikes
    private sealed class LocalAssemblyBinder : DefaultSerializationBinder
    {
        private static readonly Assembly Ours = typeof(Configuration).Assembly;
        private static readonly string OurName = Ours.GetName().Name!;

        public override Type BindToType(string? assemblyName, string typeName)
        {
            // Whole name, not ours first: a runtime generic can take our types as arguments
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

    // Settings existed but couldn't be read, so saving is refused and defaults never overwrite them
    private static bool LoadFailed;

    // Hosted, asking Dalamud hands back the host's config object
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

            // Written beside and moved in, so a failed write leaves no half-file
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
