global using System;
global using System.Collections.Generic;
global using System.Drawing;
global using System.IO;
global using System.Linq;
global using System.Threading;
global using System.Windows.Forms;

[assembly: System.Reflection.AssemblyVersion("1.3.0.0")]
[assembly: System.Reflection.AssemblyTitle("Gestionnaire RGB")]

namespace RgbManager;

/// <summary>Petits utilitaires absents de .NET Framework 4.8.</summary>
static class Compat
{
    public static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

    public static T[] Slice<T>(T[] source, int start, int length)
    {
        var result = new T[length];
        Array.Copy(source, start, result, 0, length);
        return result;
    }

    public static T[] From<T>(T[] source, int start) => Slice(source, start, source.Length - start);
}
