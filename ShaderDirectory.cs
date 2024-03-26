using System.Collections.Frozen;

namespace SeeVulkan;

class ShaderDirectory
{
    public string[] ShaderNames;
    Dictionary<string, long> timestamps = [];
    public Dictionary<string, byte[]> ShaderCodes = [];

    public FrozenSet<string> Extensions = new HashSet<string>() {
        ".rchit", ".rmiss", ".rgen", ".comp"
    }.ToFrozenSet();

    string path;

    public ShaderDirectory(string path)
    {
        this.path = path;

        List<string> names = [];
        foreach (var file in Directory.EnumerateFiles(path))
        {
            if (!Extensions.Contains(Path.GetExtension(file)))
                continue;
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
        try
        {
            foreach (var name in ShaderNames)
            {
                var fname = Path.Join(path, name);
                long newTime = File.GetLastWriteTime(fname).ToFileTime();
                if (newTime > timestamps[name])
                {
                    // Update the time first, so we don't recompile forever if glslc failed
                    timestamps[name] = newTime;
                    var code = Shader.CompileShader(fname);
                    ShaderCodes[name] = code;
                    update = true;
                }
            }
        }
        catch
        {
            // If compilation fails, we pretend the file hadn't changed. Error message will be output
            // to the console by glslc
            return false;
        }
        return update;
    }
}


