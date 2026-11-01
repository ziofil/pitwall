using System.Text;

namespace Pitwall.Console;

public static class Util
{
    public static string Sanitize(string str) => str.Replace("\e", "<ESC>").TrimEnd((char)0);

    public static string Sanitize(byte[] bytes) => Sanitize(Encoding.ASCII.GetString(bytes));
}
