namespace SeeVulkan;

public class VulkanComponent
{
    protected VulkanRayDevice rayDevice;
    protected Vk vk => rayDevice.Vk;
    protected Device device => rayDevice.Device;

    protected VulkanComponent(VulkanRayDevice rayDevice)
    {
        this.rayDevice = rayDevice;
    }
}
