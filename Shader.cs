using SeeVulkan;

unsafe class Shader : IDisposable
{
    VulkanRayDevice rayDevice;
    Vk vk => rayDevice.Vk;
    Device device => rayDevice.Device;

    public ShaderModule ShaderModule;

    public Shader(VulkanRayDevice device, string code, string ext)
    {
        rayDevice = device;
        ShaderModule = CreateShaderModule(CompileShader(code, ext));
    }

    public Shader(VulkanRayDevice device, byte[] code)
    {
        rayDevice = device;
        ShaderModule = CreateShaderModule(code);
    }

    public void Dispose()
    {
        vk.DestroyShaderModule(device, ShaderModule, null);
    }

    ShaderModule CreateShaderModule(byte[] code)
    {
        fixed (byte* codePtr = code)
        {
            ShaderModuleCreateInfo createInfo = new()
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)code.Length,
                PCode = (uint*)codePtr
            };
            CheckResult(vk.CreateShaderModule(device, createInfo, null, out var shaderModule), nameof(vk.CreateShaderModule));
            return shaderModule;
        }
    }

    public static byte[] CompileShader(string code, string ext)
    {
        var guid = Guid.NewGuid();
        string infile = $"{guid}.{ext}";
        string outfile = $"{guid}.spv";

        File.WriteAllText(infile, code);

        byte[] bytes = null;
        try
        {
            string glslcExe = "glslc"; // TODO assumes it is in the path - better solution?
            var p = System.Diagnostics.Process.Start(glslcExe, ["--target-env=vulkan1.3", infile, "-o", outfile]);
            p.WaitForExit();
            if (p.ExitCode != 0)
                throw new Exception("glslc shader compilation failed");
            bytes = File.ReadAllBytes(outfile);
        }
        finally
        {
            if (File.Exists(infile)) File.Delete(infile);
            if (File.Exists(outfile)) File.Delete(outfile);
        }

        return bytes;
    }

    public static byte[] CompileShader(string filename)
    {
        var guid = Guid.NewGuid();
        string outfile = $"{guid}.spv";

        byte[] bytes = null;
        try
        {
            string glslcExe = "glslc"; // TODO assumes it is in the path - better solution?
            var p = System.Diagnostics.Process.Start(glslcExe, ["--target-env=vulkan1.3", filename, "-o", outfile]);
            p.WaitForExit();
            if (p.ExitCode != 0)
                throw new Exception("glslc shader compilation failed");
            bytes = File.ReadAllBytes(outfile);
        }
        finally
        {
            if (File.Exists(outfile)) File.Delete(outfile);
        }

        return bytes;
    }
}
