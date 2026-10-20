using Smw2Pc.Recompiler;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: Smw2Pc.Recompiler <rom> <output-directory> [--frames 3600] [--profiles attract,play,exercise,wide-play,wide-exercise]");
    return 2;
}

try
{
    var frames = 3600;
    string[]? profiles = null;
    uint[]? entryPoints = null;
    for (var i = 2; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--frames": frames = int.Parse(args[++i]); break;
            case "--profiles": profiles = args[++i].Split(','); break;
            case "--entry-points":
                entryPoints = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(ParseAddress).ToArray();
                break;
            default: throw new ArgumentException($"Unknown option {args[i]}.");
        }
    }
    RomCompilation.Generate(args[0], args[1], frames, profiles, Console.WriteLine, entryPoints: entryPoints);
    Console.WriteLine($"Generated source and proof: {Path.GetFullPath(args[1])}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}

static uint ParseAddress(string text)
{
    var address = text.Trim();
    if (address.StartsWith('$')) address = address[1..];
    else if (address.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) address = address[2..];
    address = address.Replace(":", string.Empty, StringComparison.Ordinal);
    if (address.Length is 0 or > 6 || !uint.TryParse(address,
            System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var value))
        throw new ArgumentException($"Invalid ROM entry point '{text}'. Use a hexadecimal address such as 00C3D1.");
    return value;
}
