namespace SoftMedia.Server.Services.Abstractions;

public interface IFileSystem
{
    bool DirectoryExists(string path);
    string[] GetFiles(string path, string searchPattern, SearchOption searchOption);
    string GetExtension(string path);
    string GetFileNameWithoutExtension(string path);
    long GetFileLength(string path);
    DateTime GetLastWriteTimeUtc(string path);
}

public class FileSystem : IFileSystem
{
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public string[] GetFiles(string path, string searchPattern, SearchOption searchOption)
        => Directory.GetFiles(path, searchPattern, searchOption);

    public string GetExtension(string path) => Path.GetExtension(path);

    public string GetFileNameWithoutExtension(string path) => Path.GetFileNameWithoutExtension(path);

    public long GetFileLength(string path) => new FileInfo(path).Length;

    public DateTime GetLastWriteTimeUtc(string path) => new FileInfo(path).LastWriteTimeUtc;
}
