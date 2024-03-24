namespace SeeVulkan;

unsafe class RayAccelBase
{
    protected VulkanRayDevice rayDevice;
    protected Vk vk => rayDevice.Vk;
    protected Device device => rayDevice.Device;
    protected KhrAccelerationStructure accel;

    protected RayAccelBase(VulkanRayDevice device)
    {
        rayDevice = device;
        if (!vk.TryGetDeviceExtension(device.Instance, device.Device, out accel))
            throw new Exception($"Could not load device extension: {KhrAccelerationStructure.ExtensionName} - is it in the list of requested extensions?");
    }

    protected VulkanBuffer CreateAccelBuffer(ulong size)
    {
        BufferCreateInfo bufferCreateInfo = new() {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = BufferUsageFlags.AccelerationStructureStorageBitKhr | BufferUsageFlags.ShaderDeviceAddressBit,
        };
        CheckResult(vk.CreateBuffer(device, bufferCreateInfo, null, out var buffer), nameof(vk.CreateBuffer));

        vk.GetBufferMemoryRequirements(device, buffer, out var memoryRequirements);

        MemoryAllocateFlagsInfo memoryAllocateFlagsInfo = new() {
            SType = StructureType.MemoryAllocateFlagsInfo,
            Flags = MemoryAllocateFlags.AddressBitKhr,
        };

        MemoryAllocateInfo memoryAllocateInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            PNext = &memoryAllocateFlagsInfo,
            AllocationSize = memoryRequirements.Size,
            MemoryTypeIndex = rayDevice.GetMemoryTypeIdx(memoryRequirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit).Value
        };
        CheckResult(vk.AllocateMemory(device, &memoryAllocateInfo, null, out var memory), nameof(vk.AllocateMemory));
        CheckResult(vk.BindBufferMemory(device, buffer, memory, 0), nameof(vk.BindBufferMemory));

        return new(rayDevice, buffer, memory);
    }

    protected VulkanBuffer CreateScratchBuffer(ulong size)
    {
        BufferCreateInfo bufferCreateInfo = new()
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit
        };
        CheckResult(vk.CreateBuffer(device, &bufferCreateInfo, null, out var buffer), nameof(vk.CreateBuffer));

        vk.GetBufferMemoryRequirements(device, buffer, out var memoryRequirements);

        MemoryAllocateFlagsInfo memoryAllocateFlagsInfo = new()
        {
            SType = StructureType.MemoryAllocateFlagsInfo,
            Flags = MemoryAllocateFlags.AddressBitKhr
        };

        MemoryAllocateInfo memoryAllocateInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            PNext = &memoryAllocateFlagsInfo,
            AllocationSize = memoryRequirements.Size,
            MemoryTypeIndex = rayDevice.GetMemoryTypeIdx(memoryRequirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit).Value,
        };
        CheckResult(vk.AllocateMemory(device, &memoryAllocateInfo, null, out var memory), nameof(vk.AllocateMemory));
        CheckResult(vk.BindBufferMemory(device, buffer, memory, 0), nameof(vk.BindBufferMemory));

        return new(rayDevice, buffer, memory);
    }
}
