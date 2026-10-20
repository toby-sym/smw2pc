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
    for (var i = 2; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--frames": frames = int.Parse(args[++i]); break;
            case "--profiles": profiles = args[++i].Split(','); break;
            default: throw new ArgumentException($"Unknown option {args[i]}.");
        }
    }
    RomCompilation.Generate(args[0], args[1], frames, profiles, Console.WriteLine);
    Console.WriteLine($"Generated source and proof: {Path.GetFullPath(args[1])}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}
