using System.IO;
using System.Linq;

namespace Content.Server._CyberPunk.Wasm;

/// <summary>
/// Programs that come with the OS and run by name though they aren't on the disk, written in Wire next to this
/// file and built when the server starts. Players read their source with <c>new NAME PROGRAM</c>.
/// </summary>
public static class SystemPrograms
{
    /// <summary>The text editor, a port of Switchboard's <c>wasm/system/nano</c>.</summary>
    public const string Nano = "nano";

    /// <summary>The deck's weapon, a port of Switchboard's <c>wasm/system/blade</c>.</summary>
    public const string Blade = "blade";

    /// <summary>The deck's shield, a port of Switchboard's <c>wasm/system/ward</c>.</summary>
    public const string Ward = "ward";

    /// <summary>The ICE that comes with every computer, a port of Switchboard's <c>wasm/system/ice_basic</c>.</summary>
    public const string IceBasic = "ice_basic";

    /// <summary>Every system program's name.</summary>
    public static readonly string[] Names = [Nano, Blade, Ward, IceBasic];

    // Read once, before anything asks: hosts are made on several threads at once in tests.
    private static readonly Dictionary<string, string> Sources = Names.ToDictionary(name => name, Load);

    /// <summary>
    /// A system program's Wire source, or null if there's no such program.
    /// </summary>
    public static string? Source(string name)
    {
        return Sources.GetValueOrDefault(name);
    }

    private static string Load(string name)
    {
        using var stream = typeof(SystemPrograms).Assembly.GetManifestResourceStream($"CyberPunk14.{name}.wire")
                           ?? throw new InvalidOperationException($"{name}.wire isn't embedded in Content.Server.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
