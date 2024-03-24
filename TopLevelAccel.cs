namespace SeeVulkan;

unsafe class TopLevelAccel : RayAccelBase, IDisposable
{
    VulkanBuffer instancesBuffer, accelBuffer;

    public TopLevelAccel(VulkanRayDevice rayDevice, ReadOnlySpan<MeshAccel> meshAccels) : base(rayDevice)
    {
        TransformMatrixKHR matrix;
        new Span<float>(matrix.Matrix, 12).Clear();
        matrix.Matrix[0] = 1.0f;
        matrix.Matrix[5] = 1.0f;
        matrix.Matrix[10] = 1.0f;

        var instances = new AccelerationStructureInstanceKHR[meshAccels.Length];
        for (int i = 0; i < meshAccels.Length; ++i)
        {
            AccelerationStructureInstanceKHR instance = new()
            {
                Transform = matrix,
                InstanceCustomIndex = 0,
                Mask = 0xFF,
                InstanceShaderBindingTableRecordOffset = 0,
                Flags = GeometryInstanceFlagsKHR.TriangleFacingCullDisableBitKhr,
                AccelerationStructureReference = meshAccels[i].DeviceAddress
            };
            instances[i] = instance;
        }

        instancesBuffer = VulkanBuffer.Make<AccelerationStructureInstanceKHR>(rayDevice,
            BufferUsageFlags.ShaderDeviceAddressBit | BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            instances
        );

        AccelerationStructureGeometryKHR accelerationStructureGeometry = new()
        {
            SType = StructureType.AccelerationStructureGeometryKhr,
            GeometryType = GeometryTypeKHR.InstancesKhr,
            Flags = GeometryFlagsKHR.OpaqueBitKhr,
            Geometry = new() {
                Instances = new() {
                    SType = StructureType.AccelerationStructureGeometryInstancesDataKhr,
                    ArrayOfPointers = false,
                    Data = new(instancesBuffer.DeviceAddress)
                }
            }
        };

        AccelerationStructureBuildGeometryInfoKHR accelBuildGeometryInfo = new()
        {
            SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
            Type = AccelerationStructureTypeKHR.TopLevelKhr,
            Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr,
            GeometryCount = 1,
            PGeometries = &accelerationStructureGeometry
        };

        accel.GetAccelerationStructureBuildSizes(device, AccelerationStructureBuildTypeKHR.DeviceKhr,
            &accelBuildGeometryInfo, 1, out var accelBuildSizesInfo);
        accelBuffer = CreateAccelBuffer(accelBuildSizesInfo.AccelerationStructureSize);

        AccelerationStructureCreateInfoKHR createInfo = new()
        {
            SType = StructureType.AccelerationStructureCreateInfoKhr,
            Buffer = accelBuffer.Buffer,
            Size = accelBuildSizesInfo.AccelerationStructureSize,
            Type = AccelerationStructureTypeKHR.TopLevelKhr,
        };
        accel.CreateAccelerationStructure(device, createInfo, null, out topLevelAccel);
        accelBuildGeometryInfo.DstAccelerationStructure = topLevelAccel;

        var scratchBuffer = CreateScratchBuffer(accelBuildSizesInfo.BuildScratchSize);
        accelBuildGeometryInfo.ScratchData = new() { DeviceAddress = scratchBuffer.DeviceAddress };

        AccelerationStructureBuildRangeInfoKHR accelerationStructureBuildRangeInfo = new()
        {
            PrimitiveCount = 1,
            PrimitiveOffset = 0,
            FirstVertex = 0,
            TransformOffset = 0
        };

        var commandBuffer = rayDevice.StartOneTimeCommand();

        var buildInfoArray = stackalloc[] { accelBuildGeometryInfo };
        var buildRangeInfoArray = stackalloc[] { &accelerationStructureBuildRangeInfo };
        accel.CmdBuildAccelerationStructures(commandBuffer, 1, buildInfoArray, buildRangeInfoArray);

        rayDevice.RunAndDeleteOneTimeCommand(commandBuffer, rayDevice.GraphicsQueue);

        AccelerationStructureDeviceAddressInfoKHR accelerationDeviceAddressInfo = new()
        {
            SType = StructureType.AccelerationStructureDeviceAddressInfoKhr,
            AccelerationStructure = topLevelAccel
        };
        deviceAddress = accel.GetAccelerationStructureDeviceAddress(device, &accelerationDeviceAddressInfo);

        scratchBuffer.Dispose();
    }

    AccelerationStructureKHR topLevelAccel;
    public AccelerationStructureKHR Handle => topLevelAccel;
    ulong deviceAddress;

    public void Dispose()
    {
        instancesBuffer.Dispose();
        accelBuffer.Dispose();

        accel.DestroyAccelerationStructure(device, topLevelAccel, null);
    }
}