using System.Reflection;

namespace SeeVulkan;

class VulkanUtils
{
    public const uint VK_SHADER_UNUSED_KHR = ~0U;
    public const ulong VK_WHOLE_SIZE = ~0UL;

    public static void CheckResult(Result result, string methodName)
    {
        if (result != Result.Success)
            throw new Exception($"{methodName} failed with return code {result}");
    }

    public static uint AlignedSize(uint value, uint alignment) => (value + alignment - 1) & ~(alignment - 1);

    public static string ReadResourceText(string filename)
    {
        var assembly = typeof(RayTracingPipeline).GetTypeInfo().Assembly;
        var stream = assembly.GetManifestResourceStream($"{assembly.GetName().Name}.{filename}")
            ?? throw new FileNotFoundException("resource file not found", filename);
        return new StreamReader(stream).ReadToEnd();
    }
}