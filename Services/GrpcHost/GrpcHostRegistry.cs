using System;
using System.Collections.Generic;
using System.Linq;

namespace Hotkey_Translator.Services.GrpcHost;

internal sealed class GrpcHostRegistry
{
    private readonly IReadOnlyList<GrpcHostDescriptor> _descriptors;
    private readonly Dictionary<string, GrpcHostDescriptor> _descriptorMap;

    public GrpcHostRegistry(IReadOnlyList<GrpcHostDescriptor> descriptors)
    {
        _descriptors = descriptors;
        _descriptorMap = descriptors.ToDictionary(
            descriptor => descriptor.HostId,
            StringComparer.Ordinal);
    }

    public IReadOnlyList<GrpcHostDescriptor> All => _descriptors;

    public bool TryGet(string hostId, out GrpcHostDescriptor descriptor)
    {
        return _descriptorMap.TryGetValue(hostId, out descriptor!);
    }
}
