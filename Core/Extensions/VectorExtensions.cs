using System.Numerics;

namespace Magic.Extensions;

public static class VectorExtensions
{
    public static Vector3 Left(this Vector3 vec)
    {
        return new(1, 0, 0);
    }

    public static Vector3 Right(this Vector3 vec)
    {
        return new(-1, 0, 0);
    }

    public static Vector3 Up(this Vector3 vec)
    {
        return new(0, 1, 0);
    }

    public static Vector3 Down(this Vector3 vec)
    {
        return new(0, -1, 0);
    }

    public static Vector3 Forward(this Vector3 vec)
    {
        return new(0, 0, 1);
    }

    public static Vector3 Back(this Vector3 vec)
    {
        return new(0, 0, -1);
    }
}
