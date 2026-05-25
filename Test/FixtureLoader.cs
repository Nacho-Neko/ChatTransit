using System.Reflection;
using System.Text;

namespace ChatTransit.Tests;

internal static class FixtureLoader
{
    private static readonly Assembly Asm = typeof(FixtureLoader).Assembly;

    public static byte[] LoadBytes(string name)
    {
        var resourceName = Asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"Fixture '{name}' not found. Available: {string.Join(", ", Asm.GetManifestResourceNames())}");

        using var stream = Asm.GetManifestResourceStream(resourceName)!;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    public static string LoadString(string name) => Encoding.UTF8.GetString(LoadBytes(name));
}
