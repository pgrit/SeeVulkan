# SeeVulkan

**WIP**

A GPU renderer compatible with [SeeSharp](https://github.com/pgrit/SeeSharp)

## Requirements

- A GPU supporting (or emulating) hardware ray tracing
- Vulkan SDK, shaders are compiled on-the-fly and hot-reloaded when they update, so glslc must be in the path
- .NET 8.0

## Compile and run

```
dotnet run --project ./SeeVulkan
```

## Missing features

Compared to SeeSharp, the following features are currently missing:

- DiffuseMaterial
- EnvironmentMap
- Bidirectional rendering algorithms / vertex cache / etc