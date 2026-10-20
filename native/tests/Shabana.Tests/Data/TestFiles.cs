namespace Shabana.Tests.Data;

internal static class TestFiles
{
    /// <summary>
    /// Copies a file that another handle (SQLite) may hold open for writing. <c>File.Copy</c> asks for read access that forbids concurrent writers
    /// and fails with a sharing violation on Windows; this shares like SQLite itself does. Models "the image a power cut would leave on disk".
    /// </summary>
    public static void CopyShared(string from, string to)
    {
        using var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }

    public static byte[] ReadAllBytesShared(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        input.CopyTo(ms);
        return ms.ToArray();
    }
}
