using System.Security.Cryptography;

namespace MediaEngine.Web.Services.Ui;

/// <summary>
/// Makes the 16-character temporary password an administrator can hand over. It leaves out look-alike characters
/// (0/O, 1/l/I) so it can be read out or typed without mistakes, and is far longer than the 12-character minimum.
/// </summary>
public static class TemporaryPasswordGenerator
{
    public const int Length = 16;
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    public static string Generate()
    {
        var characters = new char[Length];
        for (var i = 0; i < characters.Length; i++)
        {
            characters[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(characters);
    }
}
