using System.Collections.Frozen;
using System.Text.RegularExpressions;
using SeeSharp.Common;

namespace SeeVulkan;

public class ShaderDirectory
{
    public string[] ShaderNames;
    Dictionary<string, long> timestamps = [];
    public Dictionary<string, byte[]> ShaderCodes = [];

    public FrozenSet<string> Extensions = new HashSet<string>() {
        ".rchit", ".rmiss", ".rgen", ".comp"
    }.ToFrozenSet();

    Dictionary<string, List<(string, long)>> shaderDependencies = [];

    string path;

    public List<(string, long)> ScanDependencies(string fname)
    {
        List<(string, long)> dependencies = [];

        var lines = File.ReadAllLines(fname);
        foreach (var line in lines)
        {
            var match = Regex.Match(line, "\\s*#include\\s*\"(.*)\"\\s*");
            if (match.Success)
            {
                string depname = match.Groups[1].Value;
                string deppath = Path.Join(Path.GetDirectoryName(fname), depname);
                dependencies.Add((deppath, File.GetLastWriteTime(deppath).ToFileTime()));
                dependencies.AddRange(ScanDependencies(deppath));
            }
        }

        return dependencies;
    }

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
            shaderDependencies[name] = ScanDependencies(fname);
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
            try
            {
                // Check if any dependency was changed -- change time is reset below automatically
                bool depUpdate = false;
                foreach (var (dep, time) in shaderDependencies[name])
                {
                    if (File.GetLastWriteTime(dep).ToFileTime() > time)
                    {
                        depUpdate = true;
                        break;
                    }
                }

                // Check if the shader itself was updated
                var fname = Path.Join(path, name);
                long newTime = File.GetLastWriteTime(fname).ToFileTime();
                if (depUpdate || newTime > timestamps[name])
                {
                    // Update the time first, so we don't recompile forever if glslc failed
                    timestamps[name] = newTime;

                    // Rescan dependencies in case an include was changed (also right away to avoid compile loop)
                    shaderDependencies[name] = ScanDependencies(fname);
                    var code = Shader.CompileShader(fname);
                    ShaderCodes[name] = code;
                    update = true;

                    Logger.Log($"{name} recompiled successfully");
                }
            }
            catch(IOException)
            {
                // The file is likely open in another process, e.g., because the editor is in the middle of saving it.
                // Let's wait until the next tick and try again.
            }
            catch(Exception exc)
            {
                Logger.Error($"{name}: recompile failed: {exc.Message}");
            }
        }

        return update;
    }
}


