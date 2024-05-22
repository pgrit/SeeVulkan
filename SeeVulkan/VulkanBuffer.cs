namespace SeeVulkan;

unsafe class VulkanBuffer : IDisposable
{
    public Buffer Buffer;
    public DeviceMemory Memory;

    VulkanRayDevice rayDevice;
    Vk vk => rayDevice.Vk;
    Device device => rayDevice.Device;

    public ulong DeviceAddress => rayDevice.GetBufferDeviceAddress(Buffer);

    public static VulkanBuffer Make<T>(VulkanRayDevice device, BufferUsageFlags usageFlags, MemoryPropertyFlags memoryPropertyFlags, ReadOnlySpan<T> data)
    {
        var b = new VulkanBuffer(device);
        b.CreateBuffer(usageFlags, memoryPropertyFlags, data);
        return b;
    }

    private VulkanBuffer(VulkanRayDevice device)
    {
        rayDevice = device;
    }

    public VulkanBuffer(VulkanRayDevice device, Buffer buffer, DeviceMemory memory)
    {
        rayDevice = device;
        Buffer = buffer;
        Memory = memory;
    }

    void CreateBuffer<T>(BufferUsageFlags usageFlags, MemoryPropertyFlags memoryPropertyFlags, ReadOnlySpan<T> data)
    {
        #pragma warning disable CS8500 // We assume sizeof(T) is fine, so let's ignore the warning
        ulong size = (ulong)(sizeof(T) * data.Length);
        #pragma warning restore CS8500

        BufferCreateInfo bufferCreateInfo = new()
        {
            SType = StructureType.BufferCreateInfo,
            SharingMode = SharingMode.Exclusive,
            Size = size,
            Usage = usageFlags
        };
        CheckResult(vk.CreateBuffer(device, bufferCreateInfo, null, out Buffer), nameof(vk.CreateBuffer));

        vk.GetBufferMemoryRequirements(device, Buffer, out var memoryRequirements);
        MemoryAllocateInfo allocateInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memoryRequirements.Size,
            MemoryTypeIndex = rayDevice.GetMemoryTypeIdx(memoryRequirements.MemoryTypeBits, memoryPropertyFlags).Value
        };
        MemoryAllocateFlagsInfoKHR allocFlagsInfo = new();
        if (usageFlags.HasFlag(BufferUsageFlags.ShaderDeviceAddressBit)) {
            allocFlagsInfo.SType = StructureType.MemoryAllocateFlagsInfoKhr;
            allocFlagsInfo.Flags = MemoryAllocateFlags.AddressBitKhr;
            allocateInfo.PNext = &allocFlagsInfo;
        }
        vk.AllocateMemory(device, allocateInfo, null, out Memory);

        void *mapped;
        CheckResult(vk.MapMemory(device, Memory, 0, size, 0, &mapped), nameof(vk.MapMemory));
        data.CopyTo(new Span<T>(mapped, (int)size));
        vk.UnmapMemory(device, Memory);

        CheckResult(vk.BindBufferMemory(device, Buffer, Memory, 0), nameof(vk.BindBufferMemory));
    }

    public void Dispose()
    {
        vk.FreeMemory(device, Memory, null);
        vk.DestroyBuffer(device, Buffer, null);
    }
}