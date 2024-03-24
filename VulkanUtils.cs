namespace SeeVulkan;

class VulkanUtils
{
    public const uint VK_SHADER_UNUSED_KHR = ~0U;

    public static void CheckResult(Result result, string methodName)
    {
        if (result != Result.Success)
            throw new Exception($"{methodName} failed with return code {result}");
    }

    public static uint AlignedSize(uint value, uint alignment) => (value + alignment - 1) & ~(alignment - 1);
}