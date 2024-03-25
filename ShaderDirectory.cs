namespace SeeVulkan;

class ShaderDirectory
{
    public string[] ShaderNames;
    Dictionary<string, long> timestamps = [];
    public Dictionary<string, byte[]> ShaderCodes = [];

    string path;

    public ShaderDirectory(string path)
    {
        this.path = path;

        List<string> names = [];
        foreach (var file in Directory.EnumerateFiles(path))
        {
            names.Add(Path.GetFileName(file));
        }
        ShaderNames = [.. names];

        foreach (var name in ShaderNames)
        {
            var fname = Path.Join(path, name);
            long newTime = File.GetLastWriteTime(fname).ToFileTime();
            var code = Shader.CompileShader(fname);
            timestamps[name] = newTime;
            ShaderCodes[name] = code;
        }
    }

    public static ShaderDirectory MakeRelativeToScript(string relativePath, [CallerFilePath] string scriptPath = null) {
        return new(Path.Join(Path.GetDirectoryName(scriptPath), relativePath));
    }

    public bool ScanForUpdates()
    {
        bool update = false;
        foreach (var name in ShaderNames)
        {
            var fname = Path.Join(path, name);
            long newTime = File.GetLastWriteTime(fname).ToFileTime();
            if (newTime > timestamps[name])
            {
                var code = Shader.CompileShader(fname);
                timestamps[name] = newTime;
                ShaderCodes[name] = code;
                update = true;
            }
        }
        return update;
    }
}


