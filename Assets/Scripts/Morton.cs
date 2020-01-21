
/*
Notes:

- integer spatial coordinates for the 2d case cannot exceed
2^16

Todo:

- C# and Burst don't let you index into their arrays with uints,
which is a bit of a pain.

*/

using System.Runtime.CompilerServices;

public static class Morton {
    [MethodImpl(MethodImplOptions.AggressiveInlining)]    
    public static int SeparateBy1(int x) {
        x &= 0x0000ffff;
        x = (x ^ (x << 8)) & 0x00ff00ff;
        x = (x ^ (x << 4)) & 0x0f0f0f0f;
        x = (x ^ (x << 2)) & 0x33333333;
        x = (x ^ (x << 1)) & 0x55555555;
        return x;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Code2d(int x, int y) {
        return SeparateBy1(x) | (SeparateBy1(y) << 1);
    }

    // Trick for offsetting Morton coded address in a 2d spatial grid

    private const int XMask = 0b01010101010101010101010101010101;
    private const int YMask = 0b00101010101010101010101010101010; // note: missing msb flag, due to signed int

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Down(int z) {
        return ((z & YMask) - 1 & YMask) | (z & XMask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Up(int z) {
        return ((z | XMask) + 1 & YMask) | (z & XMask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Left(int z) {
        return ((z & XMask) - 1 & XMask) | (z & YMask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Right(int z) {
        return ((z | YMask) + 1 & XMask) | (z & YMask);
    }

    // Tests

    public static void Test() {
        int x = 2;
        int y = 3;
        int mortonInt = Morton.Code2d(x, y);
        UnityEngine.Debug.Log(Util.ToBitString(mortonInt));
    }
}