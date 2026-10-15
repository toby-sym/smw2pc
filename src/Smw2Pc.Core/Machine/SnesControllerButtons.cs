namespace Smw2Pc.Core.Machine;

[Flags]
public enum SnesControllerButtons : ushort
{
    None = 0,
    B = 1 << 0,
    Y = 1 << 1,
    Select = 1 << 2,
    Start = 1 << 3,
    Up = 1 << 4,
    Down = 1 << 5,
    Left = 1 << 6,
    Right = 1 << 7,
    A = 1 << 8,
    X = 1 << 9,
    L = 1 << 10,
    R = 1 << 11,
}
