/*
Todo:
Move RNG bit string consumption functions into here
*/

public static class Util {
    public static string ToBitString(uint value) {
        string b = System.Convert.ToString(value, 2);
        b = b.PadLeft(32, '0');
        return b;
    }

    public static string ToBitString(int value) {
        const int SignMask = 0x8000000;
        string b = System.Convert.ToString(value, 2);
        b = b.PadLeft(32, (value & SignMask) == 1 ? '1' : '0');
        return b;
    }
}