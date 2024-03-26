namespace SeeVulkan;

unsafe class MeshAccel : RayAccelBase, IDisposable
{
    uint numTriangles;

    public VulkanBuffer VertexBuffer;
    public VulkanBuffer IndexBuffer;
    VulkanBuffer transformBuffer;

    public MeshAccel(VulkanRayDevice rayDevice, Mesh mesh)
    : base(rayDevice)
    {
        numTriangles = (uint)mesh.Indices.Length / 3;

        TransformMatrixKHR matrix;
        new Span<float>(matrix.Matrix, 12).Clear();
        matrix.Matrix[0] = 1.0f;
        matrix.Matrix[5] = 1.0f;
        matrix.Matrix[10] = 1.0f;

        VertexBuffer = VulkanBuffer.Make<Mesh.Vertex>(rayDevice,
            BufferUsageFlags.ShaderDeviceAddressBit | BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            mesh.Vertices
        );

        IndexBuffer = VulkanBuffer.Make<int>(rayDevice,
            BufferUsageFlags.ShaderDeviceAddressBit | BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            mesh.Indices
        );

        transformBuffer = VulkanBuffer.Make<float>(rayDevice,
            BufferUsageFlags.ShaderDeviceAddressBit | BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            new Span<float>(matrix.Matrix, 12)
        );

        AccelerationStructureGeometryKHR accelerationStructureGeometry = new() {
            SType = StructureType.AccelerationStructureGeometryKhr,
            Flags = GeometryFlagsKHR.OpaqueBitKhr,
            GeometryType = GeometryTypeKHR.TrianglesKhr,
            Geometry = new() {
                Triangles = new() {
                    SType = StructureType.AccelerationStructureGeometryTrianglesDataKhr,
                    VertexFormat = Format.R32G32B32Sfloat,
                    VertexData = new(VertexBuffer.DeviceAddress),
                    MaxVertex = (uint)mesh.Vertices.Length - 1,
                    VertexStride = (ulong)sizeof(Mesh.Vertex),
                    IndexType = IndexType.Uint32,
                    IndexData = new(IndexBuffer.DeviceAddress),
                    TransformData = new(transformBuffer.DeviceAddress)
                }
            },
        };

        AccelerationStructureBuildGeometryInfoKHR accelBuildGeometryInfo = new() {
            SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
            Type = AccelerationStructureTypeKHR.BottomLevelKhr,
            Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr,
            GeometryCount = 1,
            PGeometries = &accelerationStructureGeometry,
        };

        accel.GetAccelerationStructureBuildSizes(device, AccelerationStructureBuildTypeKHR.DeviceKhr,
            accelBuildGeometryInfo, numTriangles, out var buildSizeInfo);

        bottomLvlAccelBuffer = CreateAccelBuffer(buildSizeInfo.AccelerationStructureSize);

        AccelerationStructureCreateInfoKHR accelerationStructureCreateInfo = new()
        {
            SType = StructureType.AccelerationStructureCreateInfoKhr,
            Buffer = bottomLvlAccelBuffer.Buffer,
            Size = buildSizeInfo.AccelerationStructureSize,
            Type = AccelerationStructureTypeKHR.BottomLevelKhr
        };
        accel.CreateAccelerationStructure(device, &accelerationStructureCreateInfo, null, out accelStructHandle);

        var scratchBuffer = CreateScratchBuffer(buildSizeInfo.BuildScratchSize);

        accelBuildGeometryInfo = accelBuildGeometryInfo with
        {
            Mode = BuildAccelerationStructureModeKHR.BuildKhr,
            DstAccelerationStructure = accelStructHandle,
            PGeometries = &accelerationStructureGeometry,
            ScratchData = new()
            {
                DeviceAddress = scratchBuffer.DeviceAddress
            }
        };

        AccelerationStructureBuildRangeInfoKHR accelBuildRange = new()
        {
            PrimitiveCount = numTriangles,
            PrimitiveOffset = 0,
            FirstVertex = 0,
            TransformOffset = 0
        };

        var commandBuffer = rayDevice.StartOneTimeCommand();

        var buildInfoArray = stackalloc[] { accelBuildGeometryInfo };
        var buildRangeInfoArray = stackalloc[] { &accelBuildRange };
        accel.CmdBuildAccelerationStructures(commandBuffer, 1, buildInfoArray, buildRangeInfoArray);

        rayDevice.RunAndDeleteOneTimeCommand(commandBuffer, rayDevice.GraphicsQueue);

        AccelerationStructureDeviceAddressInfoKHR accelerationDeviceAddressInfo = new()
        {
            SType = StructureType.AccelerationStructureDeviceAddressInfoKhr,
            AccelerationStructure = accelStructHandle
        };
        DeviceAddress = accel.GetAccelerationStructureDeviceAddress(device, &accelerationDeviceAddressInfo);

        scratchBuffer.Dispose();
    }

    AccelerationStructureKHR accelStructHandle;

    public ulong DeviceAddress;
    private VulkanBuffer bottomLvlAccelBuffer;

    public void Dispose()
    {
        bottomLvlAccelBuffer.Dispose();
        VertexBuffer.Dispose();
        IndexBuffer.Dispose();
        transformBuffer.Dispose();

        accel.DestroyAccelerationStructure(device, accelStructHandle, null);
    }
}
