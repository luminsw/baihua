namespace Baihua.Core.Services;

public interface IVaultNameResolver
{
    string ToSafeDirectoryName(string name);
    string GetUniqueDirectoryPath(string parentDir, string name);
    string InferNameFromPath(string path);
}
